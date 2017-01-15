﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MonoMod {
    public class MonoModder : IDisposable {

        public static Action<string> DefaultLogger;
        public Action<string> Logger;

        public Relinker Relinker;

        public Stream Input;
        public Stream Output;
        public ModuleDefinition Module;
        public Dictionary<ModuleDefinition, List<ModuleDefinition>> DependencyMap = new Dictionary<ModuleDefinition, List<ModuleDefinition>>();
        public Dictionary<string, ModuleDefinition> DependencyCache = new Dictionary<string, ModuleDefinition>();
        public List<string> DependencyDirs = new List<string>();

        public List<ModuleDefinition> Mods = new List<ModuleDefinition>();

        public int CurrentRID = 0;
        public bool SkipOptimization = false;

        public ReadingMode ReadingMode = ReadingMode.Deferred;

        protected IAssemblyResolver _assemblyResolver;
        public virtual IAssemblyResolver AssemblyResolver {
            get {
                if (_assemblyResolver == null) {
                    DefaultAssemblyResolver assemblyResolver = new DefaultAssemblyResolver();
                    foreach (string dir in DependencyDirs)
                        assemblyResolver.AddSearchDirectory(dir);
                    _assemblyResolver = assemblyResolver;
                }
                return _assemblyResolver;
            }
            set {
                _assemblyResolver = value;
            }
        }

        protected ReaderParameters _readerParameters;
        public virtual ReaderParameters ReaderParameters {
            get {
                if (_readerParameters == null) {
                    _readerParameters = new ReaderParameters(ReadingMode) {
                        AssemblyResolver = AssemblyResolver
                    };
                }
                return _readerParameters;
            }
            set {
                _readerParameters = value;
            }
        }

        protected WriterParameters _writerParameters;
        public virtual WriterParameters WriterParameters {
            get {
                if (_writerParameters == null) {
                    _writerParameters = new WriterParameters() {
                        // WriteSymbols = true
                    };
                }
                return _writerParameters;
            }
            set {
                _writerParameters = value;
            }
        }

        public MonoModder() {
            Relinker = DefaultRelinker;
        }

        public void SetupLegacy() {
            ReadingMode = ReadingMode.Immediate;
        }

        public virtual void Dispose() {
            Module?.Dispose();
            Module = null;

            AssemblyResolver?.Dispose();
            AssemblyResolver = null;

            foreach (ModuleDefinition mod in Mods)
                mod?.Dispose();

            Input?.Dispose();
            Output?.Dispose();
        }

        public virtual void Log(object obj) {
            Log(obj.ToString());
        }

        public virtual void Log(string txt) {
            if (Logger != null) {
                Logger(txt);
                return;
            }
            if (DefaultLogger != null) {
                DefaultLogger(txt);
                return;
            }
            Console.Write("[MonoMod] ");
            Console.WriteLine(txt);
        }

        /// <summary>
        /// Reads the main module from the Input stream to Module.
        /// </summary>
        public virtual void Read(bool loadDependencies = true) {
            if (Module == null) {
                Log("Reading input stream into module.");
                Module = ModuleDefinition.ReadModule(Input, GenReaderParameters(true));
            }

            if (loadDependencies) MapDependencies(Module);
        }

        public virtual void MapDependencies(ModuleDefinition main) {
            if (DependencyMap.ContainsKey(main)) return;
            DependencyMap[main] = new List<ModuleDefinition>();

            foreach (AssemblyNameReference dep in main.AssemblyReferences)
                MapDependency(main, dep);
        }
        public virtual void MapDependency(ModuleDefinition main, AssemblyNameReference dep) {
            MapDependency(main, dep.Name, dep.FullName);
        }
        public virtual void MapDependency(ModuleDefinition main, string name, string fullName = null) {
            // "Fix" looping dependencies in the only place they actually are allowed to occur.
            if (name == "System" || name.StartsWith("System.") || name == "mscorlib")
                return;

            ModuleDefinition dep;
            if ((fullName != null && DependencyCache.TryGetValue(fullName, out dep)) ||
                                     DependencyCache.TryGetValue(name    , out dep)) {
                Log($"[MapDependency] {main.Name} -> {dep.Name} (({fullName}), ({name})) from cache");
                DependencyMap[main].Add(dep);
                return;
            }

            string path = null;
            foreach (string depDir in DependencyDirs) {
                path = Path.Combine(depDir, name + ".dll");
                if (!File.Exists(path))
                    path = Path.Combine(depDir, name + ".exe");
                if (!File.Exists(path))
                    path = Path.Combine(depDir, name);
                if (File.Exists(path)) break;
                else path = null;
            }

            // Check if available in GAC
            if (path == null && fullName != null) {
                // TODO use ReflectionOnlyLoad if possible
                System.Reflection.Assembly asm = null;
                try {
                    asm = System.Reflection.Assembly.Load(new System.Reflection.AssemblyName(fullName));
                } catch { }
                path = asm?.Location;
            }

            // Manually check in GAC
            if (path == null && fullName == null) {
                string os;
                System.Reflection.PropertyInfo property_platform = typeof(Environment).GetProperty("Platform", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (property_platform != null) {
                    // For mono, get from
                    // static extern PlatformID Platform
                    os = property_platform.GetValue(null, null).ToString().ToLower();
                } else {
                    // For .NET, use default value
                    os = Environment.OSVersion.Platform.ToString().ToLower();
                    // .NET also prefixes the version with a v
                }
                if (os.Contains("win")) {
                    // C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xml
                    path = Path.Combine(Environment.GetEnvironmentVariable("windir"), "Microsoft.NET", "assembly", "GAC_MSIL", name);

                    /*} else if (os.Contains("mac") || os.Contains("osx")) {
                    // TODO test GAC path for Mono on Mac
                    // should be <prefix>/lib/mono/gac, too, but what's prefix on Mac?
                } else if (os.Contains("lin") || os.Contains("unix")) {*/
                    // For now let's just pretend it's the same as with Linux...
                } else if (os.Contains("mac") || os.Contains("osx") || os.Contains("lin") || os.Contains("unix")) {
                    // <prefix>/lib/mono/gac

                    Process which = new Process();
                    which.StartInfo.FileName = "which";
                    which.StartInfo.Arguments = "mono";
                    which.StartInfo.CreateNoWindow = true;
                    which.StartInfo.RedirectStandardOutput = true;
                    which.StartInfo.UseShellExecute = false;
                    which.EnableRaisingEvents = true;

                    StringBuilder whichOutputBuilder = new StringBuilder();

                    which.OutputDataReceived += new DataReceivedEventHandler(
                        delegate (object sender, DataReceivedEventArgs e) {
                            whichOutputBuilder.Append(e.Data);
                        }
                    );
                    which.Start();
                    which.BeginOutputReadLine();
                    which.WaitForExit();
                    which.CancelOutputRead();

                    path = Directory.GetParent(whichOutputBuilder.ToString().Trim()).Parent.FullName;
                    path = Path.Combine(path, "lib", "mono", "gac", name);
                }

                if (Directory.Exists(path)) {
                    string[] versions = Directory.GetDirectories(path);
                    int highest = 0;
                    int highestIndex = 0;
                    for (int i = 0; i < versions.Length; i++) {
                        Match versionMatch = Regex.Match(versions[i].Substring(path.Length + 1), "\\d+");
                        if (!versionMatch.Success) {
                            continue;
                        }
                        int version = int.Parse(versionMatch.Value);
                        if (version > highest) {
                            highest = version;
                            highestIndex = i;
                        }
                        // Maybe check minor versions?
                    }
                    path = Path.Combine(versions[highestIndex], name + ".dll");
                } else {
                    path = null;
                }
            }

            if (path == null || !File.Exists(path)) {
                Log($"[MapDependency] {main.Name} -> (({fullName}), ({name})) not found; ignoring...");
                return;
            }

            dep = ModuleDefinition.ReadModule(path, GenReaderParameters(false));
            Log($"[MapDependency] {main.Name} -> {dep.Name} (({fullName}), ({name})) loaded");
            DependencyMap[main].Add(dep);
            DependencyCache[fullName] = dep;
            DependencyCache[name] = dep;
            MapDependencies(dep);
        }

        /// <summary>
        /// Write the modded module to the given stream or the default output.
        /// </summary>
        /// <param name="output">Output stream. If none given, default Output will be used.</param>
        public virtual void Write(Stream output = null) {
            output = output ?? Output;

            // PatchWasHere(); // FIXME

            Log("Writing modded module into output stream.");
            Module.Write(output, WriterParameters);
        }

        public virtual ReaderParameters GenReaderParameters(bool mainModule) {
            ReaderParameters _rp = ReaderParameters;
            ReaderParameters rp = new ReaderParameters(_rp.ReadingMode);
            rp.AssemblyResolver = _rp.AssemblyResolver;
            rp.MetadataResolver = _rp.MetadataResolver;
            rp.MetadataImporterProvider = _rp.MetadataImporterProvider;
            rp.ReflectionImporterProvider = _rp.ReflectionImporterProvider;
            rp.SymbolStream = _rp.SymbolStream;
            rp.SymbolReaderProvider = _rp.SymbolReaderProvider;
            rp.ReadSymbols = _rp.ReadSymbols;

            // TODO debug symbol support

            return rp;
        }


        public virtual void ReadMod(string path) {
            if (Directory.Exists(path)) {
                foreach (string mod in Directory.GetFiles(path))
                    if (mod.ToLower().EndsWith(".mm.dll"))
                        ReadMod(mod);
                return;
            }

            Mods.Add(ModuleDefinition.ReadModule(path, GenReaderParameters(false)));
        }
        public virtual void ReadMod(Stream stream) {
            Mods.Add(ModuleDefinition.ReadModule(stream, GenReaderParameters(false)));
        }


        /// <summary>
        /// Automatically mods the module, loading Input, writing the modded module to Output.
        /// </summary>
        public virtual void AutoPatch(bool read = true, bool write = true) {
            Log($"AutoPatch({read}, {write});");

            if (read) Read();

            /* WHY PRE-PATCH?
             * Custom attributes and other stuff refering to possibly new types
             * 1. could access yet undefined types that need to be copied over
             * 2. need to be copied over themselves anyway, regardless if new type or not
             * To define the order of origMethoding (first types, then references), PrePatch does
             * the "type addition" job by creating stub types, which then get filled in
             * the Patch pass.
             */

            Log("[AutoPatch] PrePatch pass");
            foreach (ModuleDefinition mod in Mods)
                PrePatchModule(mod);

            Log("[AutoPatch] Patch pass");
            foreach (ModuleDefinition mod in Mods)
                PatchModule(mod);

            /* The PatchRefs pass fixes all references referring to stuff
             * possibly added in the PrePatch or Patch passes.
             */

            Log("[AutoPatch] PatchRefs pass");
            PatchRefs();

            Log("[AutoPatch] Optimization pass");
            Optimize();

            Log("[AutoPatch] Done.");

            if (write) Write();
        }

        /// <summary>
        /// Runs some basic optimization (f.e. disables NoOptimization, removes nops)
        /// </summary>
        public virtual void Optimize() {
            if (SkipOptimization) return;
            for (int ti = 0; ti < Module.Types.Count; ti++) {
                TypeDefinition type = Module.Types[ti];
                for (int mi = 0; mi < type.Methods.Count; mi++) {
                    MethodDefinition method = type.Methods[mi];

                    method.NoInlining = false;
                    method.NoOptimization = false;

                    if (method.HasBody) {
                        for (int instri = 0; instri < method.Body.Instructions.Count; instri++) {
                            Instruction instruction = method.Body.Instructions[instri];
                            if (instruction.OpCode == OpCodes.Nop) {
                                method.Body.Instructions.RemoveAt(instri);
                                instri = Math.Max(0, instri - 1);
                                method.Body.UpdateOffsets(instri, -1);
                            }
                        }
                    }
                }
            }
        }


        public virtual IMetadataTokenProvider DefaultRelinker(IMetadataTokenProvider mtp, IGenericParameterProvider context) {
            if (mtp is TypeReference) {
                TypeReference type = (TypeReference) mtp;

                if (!Mods.Contains(type.Module)) return type; // Type isn't coming from a mod module - just return the original.

                // TODO Handle LinkTo
                // TODO What if it's in a dependency?

                // FindType works in emergency cases - try to make the non-FindType path "accurate" first!
                return Module.ImportReference(FindType(RemovePrefixes(type.FullName, type)));
                /*
                TypeReference newType = new TypeReference(type.Namespace, RemovePrefixes(type.Name), Module, Module, type.IsValueType);
                if (type.DeclaringType != null) newType.DeclaringType = Relink(type.DeclaringType, context);
                foreach (GenericParameter genParam in type.GenericParameters)
                    newType.GenericParameters.Add((GenericParameter) Relink(genParam, newType));
                return Module.ImportReference(newType);
                */
            }

            throw new InvalidOperationException($"MonoMod default relinker can't handle metadata token providers of the type {mtp.GetType()}");
        }

        public virtual IMetadataTokenProvider Relink(IMetadataTokenProvider mtp, IGenericParameterProvider context) {
            return mtp.Relink(Relinker, context);
        }
        public virtual TypeReference Relink(TypeReference type, IGenericParameterProvider context) {
            return type.Relink(Relinker, context);
        }
        public virtual MethodReference Relink(MethodReference method, IGenericParameterProvider context) {
            return method.Relink(Relinker, context);
        }
        public virtual CustomAttribute Relink(CustomAttribute attrib, IGenericParameterProvider context) {
            return attrib.Relink(Relinker, context);
        }

        public virtual TypeReference FindType(string name)
            => FindType(Module, name) ?? Module.GetType(name, true);
        protected virtual TypeReference FindType(ModuleDefinition main, string fullName) {
            TypeReference type;
            if ((type = main.GetType(fullName, false)) != null)
                return type;
            foreach (ModuleDefinition dep in DependencyMap[main])
                if ((type = FindType(dep, fullName)) != null)
                    return type;
            return null;
        }

        #region Pre-Patch Pass
        /// <summary>
        /// Pre-Patches the module (adds new types, module references, resources, ...).
        /// </summary>
        /// <param name="mod">Mod to patch into the input module.</param>
        public virtual void PrePatchModule(ModuleDefinition mod) {
            foreach (TypeDefinition type in mod.Types)
                PrePatchType(type);

            foreach (ModuleReference @ref in mod.ModuleReferences)
                if (!Module.ModuleReferences.Contains(@ref))
                    Module.ModuleReferences.Add(@ref);

            foreach (Resource res in mod.Resources)
                if (res is EmbeddedResource) 
                    Module.Resources.Add(new EmbeddedResource(
                        res.Name.StartsWith(mod.Assembly.Name.Name) ?
                            Module.Assembly.Name.Name + res.Name.Substring(mod.Assembly.Name.Name.Length) :
                            res.Name,
                        res.Attributes,
                        ((EmbeddedResource) res).GetResourceData()
                    ));
        }

        /// <summary>
        /// Patches the type (adds new types).
        /// </summary>
        /// <param name="type">Type to patch into the input module.</param>
        public virtual void PrePatchType(TypeDefinition type) {
            string typeName = RemovePrefixes(type.FullName, type);

            if (type.HasMMAttribute("Ignore") || !type.MatchingPlatform())
                return;

            // Check if type exists in target module or dependencies.
            TypeReference targetType = FindType(typeName);
            TypeDefinition targetTypeDef = targetType?.Resolve();
            if (targetType != null) {
                if (targetTypeDef != null && (type.Name.StartsWith("remove_") || type.HasMMAttribute("Remove")))
                    Module.Types.Remove(targetTypeDef);
                return;
            }

            // Add the type.
            Log($"[PrePatchType] Adding {typeName} to the target module.");

            TypeDefinition newType = new TypeDefinition(type.Namespace, type.Name, type.Attributes, type.BaseType);
            newType.AddAttribute(GetMonoModAddedCtor());

            foreach (GenericParameter genParam in type.GenericParameters)
                newType.GenericParameters.Add(genParam.Clone());

            newType.ClassSize = type.ClassSize;
            if (type.DeclaringType != null) {
                // The declaring type is existing as this is being called nestedly.
                newType.DeclaringType = Relink(type.DeclaringType, newType).Resolve();
                newType.DeclaringType.NestedTypes.Add(newType);
            } else {
                Module.Types.Add(newType);
            }
            newType.PackingSize = type.PackingSize;
            newType.SecurityDeclarations.AddRange(type.SecurityDeclarations);

            targetType = newType;
            
            PrePatchNested(type);
        }

        protected virtual void PrePatchNested(TypeDefinition type) {
            for (int i = 0; i < type.NestedTypes.Count; i++) {
                PrePatchType(type.NestedTypes[i]);
            }
        }
        #endregion

        #region Patch Pass
        /// <summary>
        /// Patches the module (adds new type members).
        /// </summary>
        /// <param name="mod">Mod to patch into the input module.</param>
        public virtual void PatchModule(ModuleDefinition mod) {
            foreach (TypeDefinition type in mod.Types)
                PatchType(type);
        }

        /// <summary>
        /// Patches the type (adds new members).
        /// </summary>
        /// <param name="type">Type to patch into the input module.</param>
        public virtual void PatchType(TypeDefinition type) {
            string typeName = RemovePrefixes(type.FullName, type);

            if (type.HasMMAttribute("Ignore") ||
                !type.MatchingPlatform()) {
                PatchNested(type);
                return;
            }

            TypeReference targetType = Module.GetType(typeName, false);
            if (targetType == null) return; // Type should've been added or removed accordingly.
            TypeDefinition targetTypeDef = targetType?.Resolve();

            if (typeName == type.FullName)
                Log($"[PatchType] Patching type {typeName}");
            else
                Log($"[PatchType] Patching type {typeName} (prefixed: {type.FullName})");

            // Add "new" custom attributes
            foreach (CustomAttribute attrib in type.CustomAttributes)
                if (!targetTypeDef.HasCustomAttribute(attrib.AttributeType.FullName))
                    targetTypeDef.CustomAttributes.Add(attrib.Clone());

            foreach (PropertyDefinition prop in type.Properties) {
                if (!targetTypeDef.HasProperty(prop)) {
                    // Add missing property
                    PropertyDefinition newProp = new PropertyDefinition(prop.Name, prop.Attributes, prop.PropertyType);
                    newProp.AddAttribute(GetMonoModAddedCtor());

                    foreach (ParameterDefinition param in prop.Parameters)
                        newProp.Parameters.Add(param.Clone());

                    foreach (CustomAttribute attrib in prop.CustomAttributes)
                        newProp.CustomAttributes.Add(attrib.Clone());

                    newProp.DeclaringType = targetTypeDef;
                    targetTypeDef.Properties.Add(newProp);
                }

                MethodDefinition getter = prop.GetMethod;
                if (getter != null && !getter.HasMMAttribute("Ignore") && getter.MatchingPlatform())
                    PatchMethod(targetTypeDef, getter);

                MethodDefinition setter = prop.SetMethod;
                if (setter != null && !setter.HasMMAttribute("Ignore") && setter.MatchingPlatform())
                    PatchMethod(targetTypeDef, setter);

                foreach (MethodDefinition method in prop.OtherMethods)
                    if (!method.HasMMAttribute("Ignore") && method.MatchingPlatform())
                        PatchMethod(targetTypeDef, method);
            }

            foreach (MethodDefinition method in type.Methods) {
                if (!AllowedSpecialName(method) || method.HasMMAttribute("Ignore") || !method.MatchingPlatform())
                    continue;
                PatchMethod(targetTypeDef, method);
            }

            if (type.HasMMAttribute("EnumReplace")) {
                for (int ii = 0; ii < targetTypeDef.Fields.Count;) {
                    if (targetTypeDef.Fields[ii].Name == "value__") {
                        ii++;
                        continue;
                    }

                    targetTypeDef.Fields.RemoveAt(ii);
                }
            }

            foreach (FieldDefinition field in type.Fields) {
                if (field.HasMMAttribute("Ignore") || field.HasMMAttribute("NoNew") || !field.MatchingPlatform())
                    continue;

                if (type.HasField(field)) continue;

                FieldDefinition newField = new FieldDefinition(field.Name, field.Attributes, field.FieldType);
                newField.AddAttribute(GetMonoModAddedCtor());
                newField.InitialValue = field.InitialValue;
                newField.Constant = field.Constant;
                foreach (CustomAttribute attrib in field.CustomAttributes)
                    newField.CustomAttributes.Add(attrib);
                targetTypeDef.Fields.Add(newField);
            }

            PatchNested(type);
        }

        protected virtual void PatchNested(TypeDefinition type) {
            for (int i = 0; i < type.NestedTypes.Count; i++) {
                PatchType(type.NestedTypes[i]);
            }
        }

        public virtual void PatchMethod(TypeDefinition type, MethodDefinition method) {
            if (method.Name.StartsWith("orig_") || method.HasMMAttribute("Original"))
                // Ignore original methods
                return;

            MethodDefinition existingMethod = type.FindMethod(method.FullName);
            MethodDefinition origMethod = type.FindMethod(RemovePrefixes(method.FullName.Replace(method.Name, method.GetOriginalName()), method.DeclaringType));

            if (existingMethod == null && method.HasMMAttribute("NoNew"))
                return;

            if (method.Name.StartsWith("replace_") || method.HasMMAttribute("Replace")) {
                method.Name = RemovePrefixes(method.Name);
                existingMethod.CustomAttributes.Clear();
                existingMethod.Attributes = method.Attributes;
                existingMethod.IsPInvokeImpl = method.IsPInvokeImpl;
                existingMethod.ImplAttributes = method.ImplAttributes;

            } else if (existingMethod != null && origMethod == null) {
                origMethod = new MethodDefinition(method.GetOriginalName(), existingMethod.Attributes & ~MethodAttributes.SpecialName & ~MethodAttributes.RTSpecialName, existingMethod.ReturnType);
                origMethod.DeclaringType = existingMethod.DeclaringType;
                origMethod.MetadataToken = GetMetadataToken(TokenType.Method);
                origMethod.Body = existingMethod.Body.Clone(origMethod);
                origMethod.Attributes = existingMethod.Attributes;
                origMethod.ImplAttributes = existingMethod.ImplAttributes;
                origMethod.IsManaged = existingMethod.IsManaged;
                origMethod.IsIL = existingMethod.IsIL;
                origMethod.IsNative = existingMethod.IsNative;
                origMethod.PInvokeInfo = existingMethod.PInvokeInfo;
                origMethod.IsPreserveSig = existingMethod.IsPreserveSig;
                origMethod.IsInternalCall = existingMethod.IsInternalCall;
                origMethod.IsPInvokeImpl = existingMethod.IsPInvokeImpl;

                origMethod.IsVirtual = false; // Fix overflow when calling orig_ method, but orig_ method already defined higher up

                foreach (GenericParameter genParam in existingMethod.GenericParameters)
                    origMethod.GenericParameters.Add(genParam.Clone());

                foreach (ParameterDefinition param in existingMethod.Parameters)
                    origMethod.Parameters.Add(param);

                foreach (CustomAttribute attrib in existingMethod.CustomAttributes)
                    origMethod.CustomAttributes.Add(attrib.Clone());

                origMethod.AddAttribute(GetMonoModOriginalCtor());

                type.Methods.Add(origMethod);
            }

            // Fix for .cctor not linking to orig_.cctor
            if (origMethod != null && origMethod.IsConstructor && origMethod.IsStatic) {
                Collection<Instruction> instructions = method.Body.Instructions;
                ILProcessor ilProcessor = method.Body.GetILProcessor();
                ilProcessor.InsertBefore(instructions[instructions.Count - 1], ilProcessor.Create(OpCodes.Call, origMethod));
            }

            if (existingMethod != null) {
                existingMethod.Body = method.Body.Clone(existingMethod);
                existingMethod.IsManaged = method.IsManaged;
                existingMethod.IsIL = method.IsIL;
                existingMethod.IsNative = method.IsNative;
                existingMethod.PInvokeInfo = method.PInvokeInfo;
                existingMethod.IsPreserveSig = method.IsPreserveSig;
                existingMethod.IsInternalCall = method.IsInternalCall;
                existingMethod.IsPInvokeImpl = method.IsPInvokeImpl;

                method = existingMethod;

            } else {
                MethodDefinition clone = new MethodDefinition(method.Name, method.Attributes, Module.TypeSystem.Void);
                clone.MetadataToken = GetMetadataToken(TokenType.Method);
                type.Methods.Add(clone);
                clone.CallingConvention = method.CallingConvention;
                clone.ExplicitThis = method.ExplicitThis;
                clone.MethodReturnType = method.MethodReturnType;
                clone.NoInlining = method.NoInlining;
                clone.NoOptimization = method.NoOptimization;
                clone.Attributes = method.Attributes;
                clone.ImplAttributes = method.ImplAttributes;
                clone.SemanticsAttributes = method.SemanticsAttributes;
                clone.DeclaringType = type;
                clone.ReturnType = method.ReturnType;
                clone.Body = method.Body.Clone(clone);
                clone.IsManaged = method.IsManaged;
                clone.IsIL = method.IsIL;
                clone.IsNative = method.IsNative;
                clone.PInvokeInfo = method.PInvokeInfo;
                clone.IsPreserveSig = method.IsPreserveSig;
                clone.IsInternalCall = method.IsInternalCall;
                clone.IsPInvokeImpl = method.IsPInvokeImpl;

                foreach (GenericParameter genParam in method.GenericParameters)
                    clone.GenericParameters.Add(genParam.Clone());

                foreach (ParameterDefinition param in method.Parameters)
                    clone.Parameters.Add(param);

                foreach (CustomAttribute attrib in method.CustomAttributes)
                    clone.CustomAttributes.Add(attrib.Clone());

                foreach (MethodReference @override in method.Overrides)
                    clone.Overrides.Add(@override);

                method = clone;
            }

            if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_"))
                foreach (PropertyDefinition property in type.Properties)
                    if (method.Name.Substring(4) == property.Name) {
                        if (method.Name[0] == 'g') {
                            property.PropertyType = method.ReturnType;
                            property.GetMethod = method;
                        } else {
                            property.SetMethod = method;
                        }
                        break;
                    }
        }
        #endregion

        #region PatchRefs Pass
        public virtual void PatchRefs() {
            foreach (TypeDefinition type in Module.Types)
                PatchRefsInType(type);
        }

        public virtual void PatchRefsInType(TypeDefinition type) {
            if (type.BaseType != null) type.BaseType = Relink(type.BaseType, type);

            // Don't foreach when modifying the collection
            for (int i = 0; i < type.Interfaces.Count; i++) {
                InterfaceImplementation interf = type.Interfaces[i];
                InterfaceImplementation newInterf = new InterfaceImplementation(Relink(interf.InterfaceType, type));
                foreach (CustomAttribute attrib in interf.CustomAttributes)
                    newInterf.CustomAttributes.Add(Relink(attrib, type));
                type.Interfaces[i] = newInterf;
            }

            foreach (CustomAttribute attrib in type.CustomAttributes)
                Relink(attrib, type);

            foreach (PropertyDefinition prop in type.Properties) {
                prop.PropertyType = Relink(prop.PropertyType, type);
                foreach (CustomAttribute attrib in prop.CustomAttributes)
                    Relink(attrib, type);
                MethodDefinition getter = prop.GetMethod;
                if (getter != null) PatchRefsInMethod(getter);
                MethodDefinition setter = prop.SetMethod;
                if (setter != null) PatchRefsInMethod(setter);
                foreach (MethodDefinition method in prop.OtherMethods)
                    PatchRefsInMethod(method);
            }

            foreach (MethodDefinition method in type.Methods)
                if (AllowedSpecialName(method))
                    PatchRefsInMethod(method);

            foreach (FieldDefinition field in type.Fields) {
                field.FieldType = Relink(field.FieldType, type);
                foreach (CustomAttribute attrib in field.CustomAttributes)
                    Relink(attrib, type);
            }

            PatchRefsInTypeNested(type);
        }

        protected virtual void PatchRefsInTypeNested(TypeDefinition type) {
            for (int i = 0; i < type.NestedTypes.Count; i++) {
                PatchRefsInType(type.NestedTypes[i]);
            }
        }

        public virtual void PatchRefsInMethod(MethodDefinition method) {
            if (method.Body == null) return;

            foreach (VariableDefinition var in method.Body.Variables)
                var.VariableType = Relink(var.VariableType, method);

            bool publicAccess = true;
            bool matchingPlatformIL = true;

            for (int instri = 0; method.HasBody && instri < method.Body.Instructions.Count; instri++) {
                Instruction instr = method.Body.Instructions[instri];
                object operand = instr.Operand;

                // MonoMod-specific in-code flag setting / ...

                // Temporarily enable matching platform, otherwise the platform data gets lost.
                // Check the next one as the array size is before the newarr.
                if (instr.Next?.OpCode == OpCodes.Newarr && (
                    (instr.Next?.Operand as TypeReference)?.FullName == "Platform" ||
                    (instr.Next?.Operand as TypeReference)?.FullName == "int"
                )) {
                    matchingPlatformIL = true;
                }

                if (operand is MethodReference && (
                    ((MethodReference) operand).DeclaringType.FullName == "MonoMod.MonoModInline" ||
                    ((MethodReference) operand).DeclaringType.FullName == "MMIL"
                )) {
                    MethodReference mr = ((MethodReference) operand);

                    if (mr.Name == "DisablePublicAccess") {
                        publicAccess = false;
                    } else if (mr.Name == "EnablePublicAccess") {
                        publicAccess = true;
                    }

                    if (mr.Name == "OnPlatform") {
                        // Crawl back until we hit "newarr Platform" or "newarr int"
                        int posNewarr = instri;
                        for (; 1 <= posNewarr && method.Body.Instructions[posNewarr].OpCode != OpCodes.Newarr; posNewarr--) ;
                        int pArrSize = method.Body.Instructions[posNewarr - 1].GetInt();
                        matchingPlatformIL = pArrSize == 0;
                        for (int ii = posNewarr + 1; ii < instri; ii += 4) {
                            // dup
                            // ldc.i4 INDEX
                            Platform plat = (Platform) method.Body.Instructions[ii + 2].GetInt();
                            // stelem.i4

                            if (PlatformHelper.Current.HasFlag(plat)) {
                                matchingPlatformIL = true;
                                break;
                            }
                        }

                        // If not matching platform, remove array code.
                        if (!matchingPlatformIL) {
                            for (int offsi = posNewarr - 1; offsi < instri; offsi++) {
                                method.Body.Instructions.RemoveAt(offsi);
                                instri = Math.Max(0, instri - 1);
                                method.Body.UpdateOffsets(instri, -1);
                                continue;
                            }
                        }
                    }

                    // Keep the method reference as modded mods may still require these.
                }

                if (!matchingPlatformIL) {
                    method.Body.Instructions.RemoveAt(instri);
                    instri = Math.Max(0, instri - 1);
                    method.Body.UpdateOffsets(instri, -1);
                    continue;
                }

                // General relinking

                if (operand is IMetadataTokenProvider) operand = Relink((IMetadataTokenProvider) operand, method);
                if (operand is TypeReference) operand = Module.ImportReference((TypeReference) operand);
                if (operand is FieldReference) operand = Module.ImportReference((FieldReference) operand);
                if (operand is MethodReference) operand = Module.ImportReference((MethodReference) operand);

                if (publicAccess) {
                    if (operand is TypeReference) ((TypeReference) operand).Resolve()?.SetPublic(true);
                    if (operand is FieldReference) ((FieldReference) operand).Resolve()?.SetPublic(true);
                    if (operand is MethodReference) ((MethodReference) operand).Resolve()?.SetPublic(true);
                }

                instr.Operand = operand;
            }
        }

        #endregion

        #region MonoMod injected types
        protected MethodDefinition _mmOriginalCtor;
        public virtual MethodReference GetMonoModOriginalCtor() {
            if (_mmOriginalCtor != null && _mmOriginalCtor.Module != Module) {
                _mmOriginalCtor = null;
            }
            if (_mmOriginalCtor != null) {
                return _mmOriginalCtor;
            }

            for (int ti = 0; ti < Module.Types.Count; ti++) {
                if (Module.Types[ti].Namespace == "MonoMod" && Module.Types[ti].Name == "MonoModOriginal") {
                    TypeDefinition type = Module.Types[ti];
                    for (int mi = 0; mi < type.Methods.Count; mi++) {
                        if (!type.Methods[mi].IsConstructor || type.Methods[mi].IsStatic) {
                            continue;
                        }
                        return _mmOriginalCtor = type.Methods[mi];
                    }
                }
            }
            Log("[MonoModOriginal] Adding MonoMod.MonoModOriginal");
            TypeDefinition attrType = new TypeDefinition("MonoMod", "MonoModOriginal", TypeAttributes.Public | TypeAttributes.Class) {
                BaseType = Module.ImportReference(typeof(Attribute))
            };
            _mmOriginalCtor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                Module.TypeSystem.Void
            );
            _mmOriginalCtor.MetadataToken = GetMetadataToken(TokenType.Method);
            _mmOriginalCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            _mmOriginalCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, Module.ImportReference(
                typeof(Attribute).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)[0]
            )));
            _mmOriginalCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            attrType.Methods.Add(_mmOriginalCtor);
            Module.Types.Add(attrType);
            return _mmOriginalCtor;
        }

        protected MethodDefinition _mmAddedCtor;
        public virtual MethodReference GetMonoModAddedCtor() {
            if (_mmAddedCtor != null && _mmAddedCtor.Module != Module) {
                _mmAddedCtor = null;
            }
            if (_mmAddedCtor != null) {
                return _mmAddedCtor;
            }

            for (int ti = 0; ti < Module.Types.Count; ti++) {
                if (Module.Types[ti].Namespace == "MonoMod" && Module.Types[ti].Name == "MonoModAdded") {
                    TypeDefinition type = Module.Types[ti];
                    for (int mi = 0; mi < type.Methods.Count; mi++) {
                        if (!type.Methods[mi].IsConstructor || type.Methods[mi].IsStatic) {
                            continue;
                        }
                        return _mmAddedCtor = type.Methods[mi];
                    }
                }
            }
            Log("[MonoModAdded] Adding MonoMod.MonoModAdded");
            TypeDefinition attrType = new TypeDefinition("MonoMod", "MonoModAdded", TypeAttributes.Public | TypeAttributes.Class) {
                BaseType = Module.ImportReference(typeof(Attribute))
            };
            _mmAddedCtor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                Module.TypeSystem.Void
            );
            _mmAddedCtor.MetadataToken = GetMetadataToken(TokenType.Method);
            _mmAddedCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            _mmAddedCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, Module.ImportReference(
                typeof(Attribute).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)[0]
            )));
            _mmAddedCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            attrType.Methods.Add(_mmAddedCtor);
            Module.Types.Add(attrType);
            return _mmAddedCtor;
        }
        #endregion


        #region Helper methods
        /// <summary>
        /// Creates a new non-conflicting MetadataToken.
        /// </summary>
        /// <param name="type">The type of the new token.</param>
        /// <returns>A MetadataToken with an unique RID for the target module.</returns>
        public virtual MetadataToken GetMetadataToken(TokenType type) {
            while (Module.LookupToken(CurrentRID | (int) type) != null) {
                ++CurrentRID;
            }
            return new MetadataToken(type, CurrentRID);
        }

        /// <summary>
        /// Checks if the method has a special name that is "allowed" to be patched.
        /// </summary>
        /// <returns><c>true</c> if the special name used in the method is allowed, <c>false</c> otherwise.</returns>
        /// <param name="method">Method to check.</param>
        public virtual bool AllowedSpecialName(MethodDefinition method) {
            if (method.DeclaringType.HasMMAttribute("Added")) {
                return true;
            }

            if (method.IsConstructor && (method.HasCustomAttributes || method.IsStatic)) {
                if (method.IsStatic) {
                    return true;
                }
                // Overriding the constructor manually is generally a horrible idea, but who knows where it may be used.
                if (method.HasMMAttribute("Constructor")) return true;
            }

            if (method.IsGetter || method.IsSetter) {
                return true;
            }

            return !method.Attributes.HasFlag(MethodAttributes.SpecialName);
        }

        /// <summary>
        /// Removes all MonoMod prefixes from the given string and its type definition.
        /// </summary>
        /// <returns>str without prefixes.</returns>
        /// <param name="str">String to remove the prefixes from or the string containing strPrefixed.</param>
        /// <param name="strPrefixed">String to remove the prefixes from when part of str.</param>
        public virtual string RemovePrefixes(string str, TypeReference type) {
            for (TypeReference type_ = type; type_ != null; type_ = type_.DeclaringType) {
                str = RemovePrefixes(str, type_.Name);
            }
            return str;
        }
        /// <summary>
        /// Removes all MonoMod prefixes from the given string.
        /// </summary>
        /// <returns>str without prefixes.</returns>
        /// <param name="str">String to remove the prefixes from or the string containing strPrefixed.</param>
        /// <param name="strPrefixed">String to remove the prefixes from when part of str.</param>
        public virtual string RemovePrefixes(string str, string strPrefixed = null) {
            strPrefixed = strPrefixed ?? str;
            str = RemovePrefix(str, "patch_", strPrefixed);
            str = RemovePrefix(str, "remove_", strPrefixed);
            str = RemovePrefix(str, "replace_", strPrefixed);
            return str;
        }
        /// <summary>
        /// Removes the prefix from the given string.
        /// </summary>
        /// <returns>str without prefix.</returns>
        /// <param name="str">String to remove the prefixes from or the string containing strPrefixed.</param>
        /// <param name="prefix">Prefix.</param>
        /// <param name="strPrefixed">String to remove the prefixes from when part of str.</param>
        public static string RemovePrefix(string str, string prefix, string strPrefixed = null) {
            strPrefixed = strPrefixed ?? str;
            if (strPrefixed.StartsWith(prefix)) {
                return str.Replace(strPrefixed, strPrefixed.Substring(prefix.Length));
            }
            return str;
        }
        #endregion

    }
}
