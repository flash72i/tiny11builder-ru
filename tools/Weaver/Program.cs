using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Usage: Weaver <targetDir> <mapApp.json> <mapCore.json> <ruDllPath> <stagePairs.json>
var dir = args[0];
var mapApp = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[1]))!;
var mapCore = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[2]))!;
var ruDll = args[3];
var stagePairs = JsonSerializer.Deserialize<List<string[]>>(File.ReadAllText(args[4]))!; // [ru, en]

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(dir);
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(ruDll))!);
var rp = new ReaderParameters { AssemblyResolver = resolver };

// Methods whose string dispatch trees (Roslyn length/char routing) must keep ORIGINAL literals.
// Their inputs are normalized RU->EN by an injected prelude instead.
var treeMethods = new HashSet<string>
{
    "System.Int32 Tiny11Builder.Core.BuildProgressText::Phase(System.String)",
    "System.ValueTuple`2<System.Double,System.Double> Tiny11Builder.Core.BuildProgressText::Range(System.String)",
    "System.Boolean Tiny11Builder.Core.BuildEngine/<>c__DisplayClass9_1::<BuildAsync>b__14(System.ValueTuple`2<System.String,System.String>)",
};

static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
{
    foreach (var t in module.Types) { yield return t; foreach (var n in NestedOf(t)) yield return n; }
}
static IEnumerable<TypeDefinition> NestedOf(TypeDefinition t)
{
    foreach (var n in t.NestedTypes) { yield return n; foreach (var x in NestedOf(n)) yield return x; }
}
static void MaterializeFieldRVA(ModuleDefinition module)
{
    int total = 0, loaded = 0;
    foreach (var type in AllTypes(module))
        foreach (var field in type.Fields)
            if (field.HasFieldRVA)
            {
                total++;
                var iv = field.InitialValue;
                if (iv is { Length: > 0 }) loaded++;
                else Console.WriteLine($"  RVA-EMPTY: {type.FullName}::{field.Name}");
            }
    Console.WriteLine($"FieldRVA materialized: {loaded}/{total}");
}
static void WriteVia(ModuleDefinition module, string path)
{
    var tmp = path + ".tmp-weave";
    module.Write(tmp);
    File.Move(tmp, path, true);
}

int PatchStrings(ModuleDefinition module, Dictionary<string, string> map, out Dictionary<string, int> hits)
{
    hits = new Dictionary<string, int>();
    int total = 0, skipped = 0;
    foreach (var type in AllTypes(module))
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            bool isTree = treeMethods.Contains(method.FullName);
            foreach (var ins in method.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && map.TryGetValue(s, out var ru))
                {
                    if (isTree) { skipped++; continue; }   // keep original literals inside dispatch trees
                    ins.Operand = ru;
                    total++;
                    hits[s] = hits.GetValueOrDefault(s) + 1;
                }
            }
        }
    if (skipped > 0) Console.WriteLine($"  tree-method literals preserved: {skipped}");
    return total;
}

// ---- build the RU->EN normalizer type inside Core ----
var corePath = Path.Combine(dir, "Tiny11Builder.Core.dll");
var appPath = Path.Combine(dir, "Tiny11Builder.dll");

// counts first (dry, in-memory)
{
    var mApp = ModuleDefinition.ReadModule(appPath, rp);
    var n = PatchStrings(mApp, mapApp, out var h);
    Console.WriteLine($"dry: app replaceable={n}");
}
var coreModule = ModuleDefinition.ReadModule(corePath, rp);
var nCoreDry = PatchStrings(coreModule, mapCore, out var hitsCore);
Console.WriteLine($"ldstr replaced: core={nCoreDry}");

var strType = coreModule.TypeSystem.String;
var boolType = coreModule.TypeSystem.Boolean;
var eq = new MethodReference("op_Equality", boolType, strType) { HasThis = false };
var sysRuntime = coreModule.AssemblyReferences.First(r => r.Name == "System.Runtime");
var ciType = new TypeReference("System.Globalization", "CultureInfo", coreModule, sysRuntime);
var tiType = new TypeReference("System.Globalization", "TextInfo", coreModule, sysRuntime);
var encType = new TypeReference("System.Text", "Encoding", coreModule, sysRuntime);
var getCult = new MethodReference("get_CurrentCulture", ciType, ciType) { HasThis = false };
var getTI = new MethodReference("get_TextInfo", tiType, ciType) { HasThis = true };
var getOem = new MethodReference("get_OEMCodePage", coreModule.TypeSystem.Int32, tiType) { HasThis = true };
var getEnc = new MethodReference("GetEncoding", encType, encType) { HasThis = false };
getEnc.Parameters.Add(new ParameterDefinition(coreModule.TypeSystem.Int32));
eq.Parameters.Add(new ParameterDefinition(strType));
eq.Parameters.Add(new ParameterDefinition(strType));

var ruPatch = new TypeDefinition("", "<RuPatch>",
    TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class,
    coreModule.TypeSystem.Object);
var mapStage = new MethodDefinition("MapStage",
    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, strType);
mapStage.Parameters.Add(new ParameterDefinition("s", ParameterAttributes.None, strType));
{
    var body = new MethodBody(mapStage);
    var il = body.GetILProcessor();
    var retArg = il.Create(OpCodes.Ldarg_0);
    var enLabels = new List<(Instruction Label, string En)>();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Brfalse, retArg);
    foreach (var pair in stagePairs)
    {
        var lbl = il.Create(OpCodes.Ldstr, pair[1]);
        enLabels.Add((lbl, pair[1]));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, pair[0]);
        il.Emit(OpCodes.Call, eq);
        il.Emit(OpCodes.Brtrue, lbl);
    }
    il.Emit(OpCodes.Br, retArg);
    foreach (var (lbl, _) in enLabels)
    {
        il.Append(lbl);
        il.Emit(OpCodes.Ret);
    }
    il.Append(retArg);
    il.Emit(OpCodes.Ret);
    mapStage.Body = body;
}
ruPatch.Methods.Add(mapStage);

// static Encoding OemEncoding(): try OEM codepage, null on any failure (null = runtime default = original behaviour)
var oemEnc = new MethodDefinition("OemEncoding", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, encType);
{
    var body = new MethodBody(oemEnc);
    var tmp = new VariableDefinition(encType);
    body.Variables.Add(tmp);
    var il = body.GetILProcessor();
    var L = il.Create(OpCodes.Ldloc, tmp);
    var retEnd = il.Create(OpCodes.Ret);
    var tryS = il.Create(OpCodes.Call, getCult);
    il.Append(tryS);
    il.Append(il.Create(OpCodes.Callvirt, getTI));
    il.Append(il.Create(OpCodes.Callvirt, getOem));
    il.Append(il.Create(OpCodes.Call, getEnc));
    il.Append(il.Create(OpCodes.Stloc, tmp));
    var leave1 = il.Create(OpCodes.Leave, L);
    il.Append(leave1);
    var catchS = il.Create(OpCodes.Pop);
    il.Append(catchS);
    il.Append(il.Create(OpCodes.Ldnull));
    il.Append(il.Create(OpCodes.Stloc, tmp));
    var leave2 = il.Create(OpCodes.Leave, L);
    il.Append(leave2);
    il.Append(L);
    il.Append(retEnd);
    body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
    {
        CatchType = coreModule.TypeSystem.Object,
        TryStart = tryS, TryEnd = catchS,
        HandlerStart = catchS, HandlerEnd = L,
    });
    oemEnc.Body = body;
}
ruPatch.Methods.Add(oemEnc);
coreModule.Types.Add(ruPatch);
Console.WriteLine($"MapStage created with {stagePairs.Count} RU->EN pairs");

// ---- inject preludes into the three tree methods ----
int injected = 0;
foreach (var type in AllTypes(coreModule))
    foreach (var method in type.Methods)
    {
        if (!treeMethods.Contains(method.FullName) || !method.HasBody) continue;
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        // find the parameter carrying the stage string
        ParameterDefinition? target = null;
        foreach (var p in method.Parameters)
        {
            if (p.ParameterType.FullName == "System.String") { target = p; break; }
            if (p.ParameterType is GenericInstanceType gi && gi.ElementType.FullName == "System.ValueTuple`2"
                && gi.GenericArguments.Count == 2 && gi.GenericArguments[0].FullName == "System.String") { target = p; break; }
        }
        if (target is null) { Console.WriteLine($"  PRELUDE-SKIP (no string param): {method.FullName}"); continue; }
        if (target.ParameterType.FullName == "System.String")
        {
            il.InsertBefore(first, il.Create(OpCodes.Ldarg, target));
            il.InsertBefore(first, il.Create(OpCodes.Call, mapStage));
            il.InsertBefore(first, il.Create(OpCodes.Starg, target));
        }
        else
        {
            var gt = (GenericInstanceType)target.ParameterType;
            var item1 = new FieldReference("Item1", strType, gt);
            il.InsertBefore(first, il.Create(OpCodes.Ldarga, target));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, item1));
            il.InsertBefore(first, il.Create(OpCodes.Call, mapStage));
            il.InsertBefore(first, il.Create(OpCodes.Ldarga, target));
            // stack: [newval][ldarga] -> need ldarga under value: redo properly
            // correct order: ldarga; <value>; stfld  -> we inserted value first; fix by reordering:
            var instrs = method.Body.Instructions;
            // remove last two and re-emit in right order
            // simpler: remove the 4 inserted and re-insert correctly
            for (int k = 0; k < 4; k++) instrs.RemoveAt(0);
            il.InsertBefore(instrs[0], il.Create(OpCodes.Ldarga, target));
            il.InsertBefore(instrs[1], il.Create(OpCodes.Ldarga, target));
            il.InsertBefore(instrs[2], il.Create(OpCodes.Ldfld, item1));
            il.InsertBefore(instrs[3], il.Create(OpCodes.Call, mapStage));
            il.InsertBefore(instrs[4], il.Create(OpCodes.Stfld, item1));
        }
        injected++;
        Console.WriteLine($"  prelude injected: {method.Name}");
    }
Console.WriteLine($"preludes injected: {injected}");


// ---- force OEM codepage decoding of redirected child-process output (DISM writes CP866/CP437) ----
{
    TypeDefinition? pr = null; MethodDefinition? run = null;
    foreach (var t in AllTypes(coreModule))
        if (t.FullName.Contains("ProcessRunner"))
            foreach (var m in t.Methods)
                if (m.Name is "RunAsync" or "MoveNext" && m.HasBody &&
                    m.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("set_StartInfo") == true))
                { pr = t; run = m; break; }
    if (run is null || !run.HasBody) { Console.WriteLine("ProcessRunner.RunAsync not found!"); }
    else
    {
        // locate info local: ldloc right before callvirt set_StartInfo
        var instrs = run.Body.Instructions;
        int setIdx = -1;
        for (int i = 0; i < instrs.Count; i++)
            if (instrs[i].OpCode == OpCodes.Callvirt && instrs[i].Operand?.ToString()?.Contains("set_StartInfo") == true) { setIdx = i; break; }
        int LocalIndex(Instruction i2)
                {
                    if (i2.OpCode == OpCodes.Ldloc_0) return 0;
                    if (i2.OpCode == OpCodes.Ldloc_1) return 1;
                    if (i2.OpCode == OpCodes.Ldloc_2) return 2;
                    if (i2.OpCode == OpCodes.Ldloc_3) return 3;
                    return ((VariableReference)i2.Operand).Index;
                }
                if (setIdx < 0 || !instrs[setIdx - 1].OpCode.Name.StartsWith("ldloc")) { Console.WriteLine("set_StartInfo anchor not found!"); }
                else
                {
                    var infoLocal = run.Body.Variables[LocalIndex(instrs[setIdx - 1])];
                    var encLocal = new VariableDefinition(encType);
                    run.Body.Variables.Add(encLocal);
                    var psType = ((MethodReference)instrs[setIdx].Operand).DeclaringType;
                    var psiType = infoLocal.VariableType; // System.Diagnostics.ProcessStartInfo
                    var setOut = new MethodReference("set_StandardOutputEncoding", coreModule.TypeSystem.Void, psiType) { HasThis = true };
                    setOut.Parameters.Add(new ParameterDefinition(encType));
                    var setErr = new MethodReference("set_StandardErrorEncoding", coreModule.TypeSystem.Void, psiType) { HasThis = true };
                    setErr.Parameters.Add(new ParameterDefinition(encType));
                    var il = run.Body.GetILProcessor();
                    var after = instrs[setIdx + 1]; // stfld process — statement boundary
                    var seq = new List<Instruction>
                    {
                        il.Create(OpCodes.Call, oemEnc),
                        il.Create(OpCodes.Stloc, encLocal),
                        il.Create(OpCodes.Ldloc, infoLocal),
                        il.Create(OpCodes.Ldloc, encLocal),
                        il.Create(OpCodes.Callvirt, setOut),
                        il.Create(OpCodes.Ldloc, infoLocal),
                        il.Create(OpCodes.Ldloc, encLocal),
                        il.Create(OpCodes.Callvirt, setErr),
                    };
                    var prev = after;
                    foreach (var s2 in seq) { il.InsertAfter(prev, s2); prev = s2; }
                    Console.WriteLine("OEM encoding injected into ProcessRunner.RunAsync");
                }
            }
        }

MaterializeFieldRVA(coreModule);
WriteVia(coreModule, corePath);

// ---- app module: strings + Ru hook ----
var module = ModuleDefinition.ReadModule(appPath, rp);
var main = module.GetType("Tiny11Builder.App.MainWindow") ?? throw new Exception("MainWindow type not found");
var ctor = main.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic) ?? throw new Exception(".ctor not found");

var ruModule = ModuleDefinition.ReadModule(ruDll, rp);
var ruName = ruModule.Assembly.Name;
var asmRef = new AssemblyNameReference(ruName.Name, ruName.Version) { Culture = ruName.Culture, Attributes = ruName.Attributes };
if (ruName.PublicKeyToken is { Length: > 0 } pkt) asmRef.PublicKeyToken = pkt;
if (!module.AssemblyReferences.Any(r => r.Name == asmRef.Name)) module.AssemblyReferences.Add(asmRef);
var ruType = new TypeReference("Tiny11Builder.Ru", "RuLocalization", module, asmRef);
var attach = new MethodReference("Attach", module.TypeSystem.Void, ruType);
attach.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
{
    var il = ctor.Body.GetILProcessor();
    var init = ctor.Body.Instructions.FirstOrDefault(i =>
        i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.Name == "InitializeComponent")
        ?? throw new Exception("InitializeComponent call not found in .ctor");
    // insert AFTER the call so every XAML element exists, but BEFORE any code that reads
    // ComboBox contents (FilterOptions) — fixes the "All"/"Все" ordering race.
    var anchor = init.Next ?? throw new Exception("no instruction after InitializeComponent");
    il.InsertBefore(anchor, il.Create(OpCodes.Ldarg_0));
    il.InsertBefore(anchor, il.Create(OpCodes.Call, attach));
    Console.WriteLine("hook injected right after InitializeComponent()");
}
var nApp = PatchStrings(module, mapApp, out var hitsApp);
Console.WriteLine($"ldstr replaced: app={nApp}");
MaterializeFieldRVA(module);
WriteVia(module, appPath);

foreach (var (k, v) in mapApp) if (!hitsApp.ContainsKey(k)) Console.WriteLine($"  APP-KEY-UNMATCHED {JsonSerializer.Serialize(k)}");
foreach (var (k, v) in mapCore) if (!hitsCore.ContainsKey(k)) Console.WriteLine($"  CORE-KEY-UNMATCHED {JsonSerializer.Serialize(k)}");

// ---- post-weave bind check: every injected MethodReference must match its resolved definition ----
{
    var chkResolver = new DefaultAssemblyResolver();
    chkResolver.AddSearchDirectory(dir);
    int checkedRefs = 0, badRefs = 0;
    var wanted = new[] { "get_TextInfo", "get_OEMCodePage", "GetEncoding", "set_StandardOutputEncoding", "set_StandardErrorEncoding", "get_CurrentCulture" };
    var chk = ModuleDefinition.ReadModule(corePath, new ReaderParameters { AssemblyResolver = chkResolver });
    foreach (var t in AllTypes(chk))
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var i in m.Body.Instructions)
            {
                if (i.Operand is MethodReference mr && Array.IndexOf(wanted, mr.Name) >= 0)
                {
                    checkedRefs++;
                    try
                    {
                        var def = mr.Resolve();
                        if (def is null) { /* facade-forwarded (System.Diagnostics.Process etc.): runtime binder follows forwarders; Cecil may not */ }
                        else if (def.HasThis != mr.HasThis || def.Parameters.Count != mr.Parameters.Count)
                        { badRefs++; Console.WriteLine($"  BIND-MISMATCH {t.Name}::{m.Name} -> {mr.FullName} hasThis={mr.HasThis} defHasThis={def.HasThis} params={mr.Parameters.Count}/{def.Parameters.Count}"); }
                    }
                    catch (Exception ex) { badRefs++; Console.WriteLine($"  BIND-UNRESOLVED {mr.FullName}: {ex.Message}"); }
                }
            }
        }
    Console.WriteLine($"bind check: {checkedRefs} injected refs, {badRefs} mismatches");
    if (badRefs > 0) throw new Exception("injected reference signature mismatch");
}

Console.WriteLine("WEAVE-OK");
