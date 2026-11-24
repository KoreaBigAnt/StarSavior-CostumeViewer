"""Run in Blender's Text Editor. File > Import/Export > StarSavior Mod Mesh.
The original vertex-id attribute must survive editing. No runtime game installation.
"""
import bpy, json, struct, hashlib, math, os, uuid
from pathlib import Path
from collections import defaultdict, deque
from mathutils import Matrix, Vector
from bpy_extras.io_utils import ImportHelper, ExportHelper
from bpy.props import StringProperty

C = Matrix(((-1,0,0,0),(0,0,-1,0),(0,1,0,0),(0,0,0,1)))
CI = C.inverted()

def safe(root, name):
    root = Path(root).resolve()
    path = (root / name).resolve()
    path.relative_to(root)
    return path

def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))

def ints(path):
    data = Path(path).read_bytes()
    if len(data) % 4: raise ValueError('Invalid index buffer')
    return [v[0] for v in struct.iter_unpack('<i', data)]

def channel(folder, meta, name):
    desc = meta['Channels'].get(name)
    if not desc: return None
    data = safe(folder, desc['File']).read_bytes()
    if hashlib.sha256(data).hexdigest().upper() != desc['SHA256'].upper():
        raise ValueError('Changed reference channel: ' + name)
    count, dim = meta['VertexCount'], desc['Components']
    if len(data) != count*dim*4: raise ValueError('Invalid reference channel size')
    values = struct.unpack('<'+'f'*(count*dim), data)
    return [tuple(values[i*dim:(i+1)*dim]) for i in range(count)]

def matrix(values):
    return Matrix(tuple(tuple(values[col*4+row] for col in range(4)) for row in range(4)))

def edited_channel(profile, rule, name, count):
    desc=rule.get(name) if rule else None
    if not desc: return None
    dim=desc['Components']; data=safe(profile,desc['File']).read_bytes()
    expected=2 if name=='TexCoord0' else 3
    if dim!=expected or len(data)!=count*dim*4: raise ValueError('Invalid imported mesh channel: '+name)
    values=struct.unpack('<'+'f'*(count*dim),data)
    if any(not math.isfinite(v) for v in values): raise ValueError('Non-finite imported mesh coordinate')
    return [tuple(values[i*dim:(i+1)*dim]) for i in range(count)]

def add_ids(mesh, name, domain, values):
    attr = mesh.attributes.new(name=name, type='INT', domain=domain)
    attr.data.foreach_set('value', values)

def make_rig(meta, folder, obj, tag):
    bones = meta['Bones']
    if not meta['Skinned']: return
    arm = bpy.data.armatures.new(tag+'_rig')
    rig = bpy.data.objects.new(tag+'_rig', arm)
    bpy.context.collection.objects.link(rig)
    rig.matrix_world=obj.matrix_world.copy()
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode='EDIT')
    created=[]
    try:
        for bone in bones:
            b=arm.edit_bones.new('b%04d_%s' % (bone['Index'], bone['Name']))
            rest=C @ matrix(bone['RestMatrix']) @ CI
            b.head=rest.translation
            direction=rest.to_3x3().col[1]
            if direction.length < 1e-8: direction=Vector((0,0,1))
            b.tail=b.head+direction.normalized()*.045
            b.matrix=rest
            created.append(b)
        for i,bone in enumerate(bones):
            if bone['Parent'] >= 0: created[i].parent=created[bone['Parent']]
        names=[b.name for b in created]
    finally:
        bpy.ops.object.mode_set(mode='OBJECT')
    groups=[obj.vertex_groups.new(name=name) for name in names]
    counts=safe(folder, meta['BoneCountsFile']).read_bytes()
    weights=list(struct.iter_unpack('<if', safe(folder, meta['BoneWeightsFile']).read_bytes()))
    if len(counts)!=meta['VertexCount'] or sum(counts)!=len(weights):
        raise ValueError('Invalid skin influence data')
    index=0
    for v, count in enumerate(counts):
        for _ in range(count):
            bone, weight=weights[index]; index+=1
            if bone<0 or bone>=len(groups) or not math.isfinite(weight) or weight<0:
                raise ValueError('Invalid bone weight')
            if weight>0: groups[bone].add([v], weight, 'REPLACE')
    mod=obj.modifiers.new('Original game rig', 'ARMATURE'); mod.object=rig
    rig.show_in_front=True
    rig['ss_note']='Reference rig. Mesh edits reuse original game bones and weights.'
    rig.select_set(False)

def import_profile(profile):
    profile=Path(profile).resolve()
    manifest=read_json(profile/'mod-manifest.json')
    if manifest['Format']!='StarSavior.AppearanceMod': raise ValueError('Invalid mod manifest')
    index=read_json(profile/'MeshEdit/mesh-index.json')
    override_file=profile/'mesh-overrides.json'
    overrides={e['RendererPath']:e for e in read_json(override_file)['Meshes']} if override_file.exists() else {}
    collection=bpy.data.collections.new('StarSavior_'+manifest['Id'][:8])
    bpy.context.scene.collection.children.link(collection)
    imported=[]
    for item in index['Meshes']:
        source=safe(profile/'MeshEdit', item['File']); meta=read_json(source); folder=source.parent
        pos=channel(folder, meta, 'Position'); uv=channel(folder, meta, 'TexCoord0')
        rule=overrides.get(meta['RendererPath'])
        if rule and rule['VertexCount']!=meta['VertexCount']: raise ValueError('Imported mesh vertex count differs')
        pos=edited_channel(profile,rule,'Position',meta['VertexCount']) or pos
        uv=edited_channel(profile,rule,'TexCoord0',meta['VertexCount']) or uv
        verts=[tuple((C @ Vector((*p[:3],1)))[:3]) for p in pos]
        faces=[]; face_ids=[]; slots=[]
        for sub in meta['SubMeshes']:
            original=ints(safe(folder, sub['OriginalIndices']))
            if hashlib.sha256(struct.pack('<'+'i'*len(original),*original)).hexdigest().upper()!=sub['OriginalIndexSHA256'].upper():
                raise ValueError('Reference topology fingerprint mismatch')
            lookup=defaultdict(deque)
            for t in range(len(original)//3): lookup[tuple(original[t*3:t*3+3])].append(t)
            visible=ints(safe(folder, sub['VisibleIndices']))
            hidden=set()
            if rule:
                for group in rule.get('Visibility',[]):
                    if group['SubMesh']==sub['SubMesh']: hidden.update(group['HiddenTriangleIds'])
            for t in range(len(visible)//3):
                tri=tuple(visible[t*3:t*3+3]); queue=lookup[tri]
                if not queue: raise ValueError('Visible triangle is not in original mesh')
                tri_id=queue.popleft()
                if tri_id in hidden: continue
                faces.append(tuple(reversed(tri))); face_ids.append(tri_id); slots.append(sub['SubMesh'])
        mesh=bpy.data.meshes.new(item['Id']+'_rest'); mesh.from_pydata(verts, [], faces); mesh.update()
        if len(mesh.vertices)!=meta['VertexCount'] or len(mesh.polygons)!=len(faces):
            raise ValueError('Blender changed the imported topology')
        add_ids(mesh,'ss_vertex_id','POINT',list(range(len(verts))))
        add_ids(mesh,'ss_triangle_id','FACE',face_ids); add_ids(mesh,'ss_submesh','FACE',slots)
        for sub in meta['SubMeshes']:
            slot=sub['SubMesh']; material=next((m for m in meta['Materials'] if m['Slot']==slot),None)
            mat=bpy.data.materials.new((material or {}).get('Name','material_'+str(slot)))
            mat.use_nodes=True
            if material:
                binding=next((b for b in material['Textures'] if b['Property'] in ('_BaseMap','_MainTex','_BaseColorMap','_AlbedoMap','_DiffuseMap')),None)
                if binding:
                    tex=mat.node_tree.nodes.new('ShaderNodeTexImage'); tex.image=bpy.data.images.load(str(safe(profile,binding['File'])),check_existing=True)
                    bsdf=next((n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED'),None)
                    if bsdf is None: bsdf=mat.node_tree.nodes.new('ShaderNodeBsdfPrincipled')
                    mat.node_tree.links.new(tex.outputs['Color'],bsdf.inputs['Base Color'])
                    mat.node_tree.links.new(tex.outputs['Alpha'],bsdf.inputs['Alpha'])
                    if hasattr(mat,'surface_render_method'): mat.surface_render_method='DITHERED'
                    elif hasattr(mat,'blend_method'): mat.blend_method='HASHED'
            mesh.materials.append(mat)
        for polygon,slot in zip(mesh.polygons,slots): polygon.material_index=slot; polygon.use_smooth=True
        if uv:
            layer=mesh.uv_layers.new(name='UVMap')
            for loop in mesh.loops: layer.data[loop.index].uv=uv[loop.vertex_index][:2]
        obj=bpy.data.objects.new(item['Id']+'_'+meta['MeshName'],mesh); collection.objects.link(obj)
        if meta.get('RendererToModel'): obj.matrix_world=C @ matrix(meta['RendererToModel']) @ CI
        obj['ss_source']='MeshEdit/'+item['File']; obj['ss_renderer']=meta['RendererPath']
        obj['ss_profile']=str(profile); obj['ss_mod_id']=manifest['Id']
        obj['ss_bind_object_matrix']=[obj.matrix_world[r][c] for r in range(4) for c in range(4)]
        make_rig(meta,folder,obj,item['Id']); imported.append(obj)
        obj.hide_set(not(meta.get('RendererActive',True) and meta.get('RendererEnabled',True)))
    bpy.ops.object.select_all(action='DESELECT')
    for obj in imported: obj.select_set(True)
    if imported: bpy.context.view_layer.objects.active=imported[0]
    return len(imported),index.get('Errors',[])

def write_binary(path, values):
    data=struct.pack('<'+'f'*len(values), *values)
    if any(not math.isfinite(v) for v in values): raise ValueError('Non-finite edited coordinate')
    path.parent.mkdir(parents=True,exist_ok=True); path.write_bytes(data)
    return {'File':None,'Components':None,'SHA256':hashlib.sha256(data).hexdigest().upper()}

def export_modified(profile):
    profile=Path(profile).resolve(); manifest=read_json(profile/'mod-manifest.json')
    current=read_json(profile/'mesh-overrides.json'); entries={e['RendererPath']:e for e in current['Meshes']}
    staged=[]
    generation=uuid.uuid4().hex[:12]
    objects=[o for o in bpy.context.selected_objects if o.type=='MESH' and 'ss_source' in o]
    if not objects: raise ValueError('Select imported StarSavior mesh objects')
    seen=set()
    for obj in objects:
        if obj['ss_mod_id']!=manifest['Id']: raise ValueError('Mesh belongs to another profile/mod ID')
        path=obj['ss_renderer']
        if path in seen: raise ValueError('Duplicate renderer object selected: '+path)
        seen.add(path)
        source=safe(profile,obj['ss_source']); meta=read_json(source); mesh=obj.data
        if obj.mode=='EDIT': obj.update_from_editmode()
        expected=obj.get('ss_bind_object_matrix',[1 if r==c else 0 for r in range(4) for c in range(4)])
        if any(abs(obj.matrix_world[r][c]-expected[r*4+c])>1e-5 for r in range(4) for c in range(4)):
            raise ValueError('Keep the original object transform; edit mesh vertices in Edit/Sculpt Mode: '+obj.name)
        attr=mesh.attributes.get('ss_vertex_id'); n=meta['VertexCount']
        ids=[v.value for v in attr.data] if attr and attr.domain=='POINT' else []
        if len(mesh.vertices)!=n or sorted(ids)!=list(range(n)):
            raise ValueError('Vertex IDs/count changed. Do not add/merge/split/remesh/subdivide vertices: '+obj.name)
        old_pos=channel(source.parent,meta,'Position'); old_uv=channel(source.parent,meta,'TexCoord0')
        old_norm=channel(source.parent,meta,'Normal')
        positions=[None]*n; normals=[None]*n; uvs=[list(v[:2]) for v in old_uv] if old_uv else None
        for vertex,original in zip(mesh.vertices,ids):
            positions[original]=tuple((CI @ Vector((*vertex.co,1)))[:3])
            normal=CI.to_3x3() @ vertex.normal
            normals[original]=tuple(normal.normalized()) if normal.length>1e-8 else (old_norm[original][:3] if old_norm else (0,1,0))
        tri_attr=mesh.attributes.get('ss_triangle_id'); sub_attr=mesh.attributes.get('ss_submesh')
        if not tri_attr or not sub_attr: raise ValueError('Original triangle attributes were lost')
        remaining=defaultdict(set); originals={s['SubMesh']:ints(safe(source.parent,s['OriginalIndices'])) for s in meta['SubMeshes']}
        uv_seen={}
        for poly in mesh.polygons:
            if len(poly.vertices)!=3: raise ValueError('New/non-triangle faces are unsupported')
            sub=sub_attr.data[poly.index].value; tri=tri_attr.data[poly.index].value
            base=originals.get(sub,[]); ref=base[tri*3:tri*3+3] if tri>=0 else []
            actual=[ids[v] for v in reversed(poly.vertices)]
            if len(ref)!=3 or actual not in [ref,ref[1:]+ref[:1],ref[2:]+ref[:2]] or tri in remaining[sub]:
                raise ValueError('New/changed triangle topology is unsupported')
            remaining[sub].add(tri)
            if uvs is not None and mesh.uv_layers.active:
                for loop_idx in poly.loop_indices:
                    vertex_id=ids[mesh.loops[loop_idx].vertex_index]; value=tuple(mesh.uv_layers.active.data[loop_idx].uv)
                    if vertex_id in uv_seen and max(abs(a-b) for a,b in zip(value,uv_seen[vertex_id]))>1e-6:
                        raise ValueError('A vertex now has multiple UVs; keep original UV seams')
                    uv_seen[vertex_id]=value; uvs[vertex_id]=value
        hidden=[{'SubMesh':s['SubMesh'],'HiddenTriangleIds':[i for i in range(len(originals[s['SubMesh']])//3) if i not in remaining[s['SubMesh']]]} for s in meta['SubMeshes']]
        subfinger=[{'SubMesh':s['SubMesh'],'OriginalIndexSHA256':s['OriginalIndexSHA256']} for s in meta['SubMeshes']]
        entry={'RendererPath':path,'SourceMesh':obj['ss_source'],'VertexCount':n,'VertexOrder':'OriginalVertexId','CoordinateSpace':'UnityRendererLocalLeftHanded','OriginalSubMeshes':subfinger,'Visibility':hidden}
        output='Meshes/'+generation+'/'+source.parent.name
        for name,values,dim in [('Position',positions,3),('Normal',normals,3),('TexCoord0',uvs,2)]:
            if values is None: continue
            relative=output+'/'+name+'.f32le'; staged.append((safe(profile,relative),[c for value in values for c in value[:dim]]))
            entry[name]={'File':relative,'Components':dim}
        entries[path]=entry
    # Validate all selected objects before writing payload or atomically replacing manifest.
    for path,values in staged: write_binary(path,values)
    document={'Format':'StarSavior.MeshOverrides','Version':1,'Meshes':list(entries.values())}
    temporary=profile/'mesh-overrides.json.pending'
    temporary.write_text(json.dumps(document,ensure_ascii=False,indent=2),encoding='utf8')
    os.replace(temporary,profile/'mesh-overrides.json')
    return len(objects)

class SS_IMPORT(bpy.types.Operator,ImportHelper):
    bl_idname='import_scene.starsavior_mod_mesh'; bl_label='StarSavior Mod Mesh'
    filename_ext='.json'
    filter_glob:StringProperty(default='*.json',options={'HIDDEN'})
    def execute(self,context):
        try:
            path=Path(self.filepath); root=path.parent.parent if path.name=='mesh-index.json' else path.parent
            count,errors=import_profile(root)
            self.report({'WARNING'} if errors else {'INFO'},'Imported %d meshes; source errors: %d'%(count,len(errors)))
            return {'FINISHED'}
        except Exception as e: self.report({'ERROR'},str(e)); return {'CANCELLED'}

class SS_EXPORT(bpy.types.Operator,ExportHelper):
    bl_idname='export_scene.starsavior_mod_mesh'; bl_label='StarSavior Modified Mesh'
    filename_ext='.json'
    filter_glob:StringProperty(default='*.json',options={'HIDDEN'})
    def execute(self,context):
        try:
            count=export_modified(Path(self.filepath).parent)
            self.report({'INFO'},'Saved %d renderer overrides to mesh-overrides.json'%count); return {'FINISHED'}
        except Exception as e: self.report({'ERROR'},str(e)); return {'CANCELLED'}

def menu_import(self,context): self.layout.operator(SS_IMPORT.bl_idname,text='StarSavior Mod Mesh (.json)')
def menu_export(self,context): self.layout.operator(SS_EXPORT.bl_idname,text='StarSavior Modified Mesh (.json)')
def register():
    for cls in (SS_IMPORT,SS_EXPORT):
        old=getattr(bpy.types,cls.__name__,None)
        if old:
            try: bpy.utils.unregister_class(old)
            except RuntimeError: pass
        bpy.utils.register_class(cls)
    bpy.types.TOPBAR_MT_file_import.append(menu_import); bpy.types.TOPBAR_MT_file_export.append(menu_export)
if __name__=='__main__': register()
