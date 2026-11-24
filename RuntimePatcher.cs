using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class RuntimePatcher
{
    static int Main(string[] args)
    {
        try
        {
            string folder=Path.GetFullPath(args[0]);
            var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(folder);
            var options=new ReaderParameters{AssemblyResolver=resolver};
            using(var viewer=ModuleDefinition.ReadModule(Path.Combine(folder,"StarSavior.CostumeViewer.dll"),options))
            {
                Patch(folder,"NKC.dll",options,module=>
                {
                    var type=module.GetType("NKC.AppLauncher");
                    var start=type.Methods.Single(method=>method.Name=="Start");
                    Replace(start,module.ImportReference(viewer.GetType("StarSaviorCostumeViewer.LocalBootstrap").Methods.Single(method=>method.Name=="Start")),true);
                    var awake=type.Methods.Single(method=>method.Name=="Awake");
                    var il=awake.Body.GetILProcessor();var first=awake.Body.Instructions[0];
                    il.InsertBefore(first,il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(first,il.Create(OpCodes.Call,module.ImportReference(viewer.GetType("StarSaviorCostumeViewer.StartupAppearance").Methods.Single(method=>method.Name=="Begin"))));
                });
                Patch(folder,"UnityEngine.CoreModule.dll",options,module=>Replace(module.GetType("UnityEngine.Application").Methods.Single(method=>method.Name=="get_persistentDataPath"),
                    module.ImportReference(viewer.GetType("StarSaviorCostumeViewer.LocalBootstrap").Methods.Single(method=>method.Name=="ProfilePath")),false));
            }
            Patch(folder,"Bs.Windows.dll",options,module=>
            {
                var method=module.GetType("Bs.Windows.WindowsExtends").Methods.Single(item=>item.Name=="CheckMultiClient");
                var names=method.Body.Instructions.Where(item=>item.OpCode==OpCodes.Ldstr&&((string)item.Operand).StartsWith("StarClientStandAlone",StringComparison.Ordinal)).ToArray();
                if(names.Length!=1)throw new InvalidDataException("Unsupported client multi-instance method");
                names[0].Operand="StarSaviorCostumeViewerPlayer";
            });
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static void Replace(MethodDefinition method,MethodReference target,bool instance)
    {
        method.CustomAttributes.Clear();method.Body.Instructions.Clear();method.Body.ExceptionHandlers.Clear();method.Body.Variables.Clear();
        var il=method.Body.GetILProcessor();if(instance)il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call,target));il.Append(il.Create(OpCodes.Ret));
    }
    static void Patch(string folder,string file,ReaderParameters options,Action<ModuleDefinition> action)
    {
        string path=Path.Combine(folder,file),temporary=path+".viewer-new";
        using(var module=ModuleDefinition.ReadModule(path,options)){action(module);module.Write(temporary);}
        File.Replace(temporary,path,null);
    }
}
