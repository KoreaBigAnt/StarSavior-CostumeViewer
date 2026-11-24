import os,sys,json,math
from pathlib import Path
import numpy as np
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'.runtime'/'review-python'))
from PIL import Image,ImageDraw,ImageFont
root=Path(sys.argv[1]); plan=json.loads((root/'review-plan.json').read_text(encoding='utf-8-sig'))
out=root/'BodyReview'/'contact';out.mkdir(exist_ok=True)
font=ImageFont.truetype('C:/Windows/Fonts/malgun.ttf',20)
reports=[]
for person in plan:
    unit=person['Unit'];base_dir=root/'BodyReview'/f'{unit}-base'
    if not (base_dir/'survey.json').exists():continue
    base=json.loads((base_dir/'survey.json').read_text(encoding='utf-8-sig'))
    cases=[s for s in ['base','min','max','fat','minfat','maxfat'] if (root/'BodyReview'/f'{unit}-{s}'/'survey.json').exists()]
    sheet=Image.new('RGB',(400*len(cases),920),(20,23,30));draw=ImageDraw.Draw(sheet)
    draw.text((12,5),f"{unit} {person['Name']}",font=font,fill='white')
    nodes=base['Body']['Nodes']; knees=[n['Position'][1] for n in nodes if n['Name'] in ('Bip001 L Calf','Bip001 R Calf')]
    knee=float(np.mean(knees)) if knees else base['Body']['Knee']
    for cidx,case in enumerate(cases):
        folder=root/'BodyReview'/f'{unit}-{case}';data=json.loads((folder/'survey.json').read_text(encoding='utf-8-sig'))
        draw.text((cidx*400+10,35),case,font=font,fill='white')
        for row,view in enumerate(['front','side']):
            photo=Image.open(folder/f'{view}.png').convert('RGB');photo.thumbnail((400,410))
            sheet.paste(photo,(cidx*400+(400-photo.width)//2,65+row*425))
        if case=='base':continue
        meshes={m['Name']:m for m in data['Meshes']};flipped=collapsed=faces=0;max_error=0;segments=[]
        for mesh in base['Meshes']:
            if mesh['Name'].lower() not in ('body','tights') and not mesh['Name'].lower().startswith('body_'):continue
            other=meshes[mesh['Name']]
            old=np.fromfile(base_dir/mesh['Positions'],dtype='<f4').reshape(-1,3).astype(np.float64)
            new=np.fromfile(folder/other['Positions'],dtype='<f4').reshape(-1,3).astype(np.float64)
            tri=np.fromfile(base_dir/mesh['Triangles'],dtype='<i4').reshape(-1,3)
            if old.shape!=new.shape:raise RuntimeError('Topology changed')
            mask=np.fromfile(base_dir/mesh['Weights'],dtype='<f4').reshape(-1,4) if 'Weights' in mesh else None
            center=old[tri].mean(axis=1);band=np.abs(center[:,1]-knee)<.075
            if mask is not None: band &= np.min(mask[tri,0],axis=1)>.95
            selected=tri[band]
            n0=np.cross(old[selected[:,1]]-old[selected[:,0]],old[selected[:,2]]-old[selected[:,0]])
            n1=np.cross(new[selected[:,1]]-new[selected[:,0]],new[selected[:,2]]-new[selected[:,0]])
            a0=np.linalg.norm(n0,axis=1);a1=np.linalg.norm(n1,axis=1);valid=a0>1e-9
            cosine=np.sum(n0*n1,axis=1)/np.maximum(1e-20,a0*a1)
            flipped+=int(np.sum(valid&(cosine<-.05)));collapsed+=int(np.sum(valid&(a1<a0*.1)));faces+=int(np.sum(valid))
            if case in ('min','max'):
                oldroot=np.array(base['Body']['RootMatrix']).reshape(4,4,order='F') if 'RootMatrix' in base['Body'] else np.eye(4)
                newroot=np.array(data['Body']['RootMatrix']).reshape(4,4,order='F') if 'RootMatrix' in data['Body'] else None
                if newroot is not None:
                    mapped=(np.c_[old,np.ones(len(old))]@(newroot@np.linalg.inv(oldroot)).T)[:,:3]
                    vertices=np.unique(selected);max_error=max(max_error,float(np.max(np.linalg.norm(mapped[vertices]-new[vertices],axis=1))) if len(vertices) else 0)
                foot=base['Body']['Floor'];hip=base['Body']['Hip'];neck=base['Body']['Neck']
                gains=[]
                for ridx,(low,high) in enumerate([(foot+.04,knee-.04),(knee+.04,hip-.04),(hip+.03,neck-.02)]):
                    region=(old[:,1]>low)&(old[:,1]<high)
                    if mask is not None: region &= mask[:,ridx+1]>.95
                    gains.append(float(np.ptp(new[region,1])/np.ptp(old[region,1])) if np.sum(region)>10 else None)
                segments.append(gains)
        reports.append(dict(unit=unit,name=person['Name'],case=case,knee_faces=faces,knee_flipped=flipped,knee_collapsed=collapsed,knee_affine_error_mm=max_error*1000,segment_gains=segments))
    sheet.save(out/f'{unit}.jpg',quality=94)
(root/'body-review-metrics.json').write_text(json.dumps(reports,ensure_ascii=False,indent=2),encoding='utf-8')
for r in reports:print(f"{r['unit']} {r['case']}: knee flipped={r['knee_flipped']} collapsed={r['knee_collapsed']} affine_error_mm={r['knee_affine_error_mm']:.3f} gains={r['segment_gains']}")
if len(reports)!=len(plan)*5 or any(r['knee_faces']==0 or r['knee_flipped'] or r['knee_collapsed'] for r in reports):
    raise SystemExit('FAIL: incomplete knee coverage or folded/collapsed triangles')
print('PASS: all planned characters have measured knee geometry in every non-baseline case.')
