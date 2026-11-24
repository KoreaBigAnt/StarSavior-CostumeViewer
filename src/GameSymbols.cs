using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace StarSaviorCostumeViewer
{
    // Public and private clients keep the same reflection contract but different metadata layouts.
    public static class GameSymbols
    {
        private static JObject symbols;
        private static JObject Data
        {
            get
            {
                if(symbols!=null)return symbols;
                if(Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_PUBLIC")=="0")return symbols=new JObject();
                string path=Path.Combine(Environment.GetEnvironmentVariable("STARSAVIOR_VIEWER_ROOT")??".","engine-overrides","public-symbols.json");
                return symbols=File.Exists(path)?JObject.Parse(File.ReadAllText(path)):new JObject();
            }
        }
        private static string Key(Type type)=> (type.IsGenericType?type.GetGenericTypeDefinition():type).FullName;
        private static string Name(Type type,string name,string kind)
        {
            for(var at=type;at!=null;at=at.BaseType)
            {
                var value=Data[kind]?[Key(at)+"|"+name];
                if(value!=null)return (string)value;
            }
            return name;
        }
        internal static string MethodName(Type type,string name)=>Name(type,name,"Methods");
        internal static int FieldRow(Type type,int row)
        {
            for(var at=type;at!=null;at=at.BaseType)
            {
                var value=Data["FieldRows"]?[Key(at)+"|"+row];if(value!=null)return (int)value;
            }
            return row;
        }
        public static MethodInfo GetMethod(Type type,string name)=>type.GetMethod(MethodName(type,name));
        public static MethodInfo GetMethod(Type type,string name,BindingFlags flags)=>type.GetMethod(MethodName(type,name),flags);
        public static MethodInfo GetMethod(Type type,string name,BindingFlags flags,Binder binder,Type[] types,ParameterModifier[] modifiers)
            =>type.GetMethod(MethodName(type,name),flags,binder,types,modifiers);
        public static FieldInfo GetField(Type type,string name,BindingFlags flags)=>type.GetField(Name(type,name,"Fields"),flags);
        public static FieldInfo GetField(Type type,string name)=>type.GetField(Name(type,name,"Fields"));
        public static MethodBase ResolveMethod(Module module,int token)
        {
            var value=Data["Tokens"]?[module.Assembly.GetName().Name+"|"+token];
            return module.ResolveMethod(value==null?token:(int)value);
        }
    }
}
