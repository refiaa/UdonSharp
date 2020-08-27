﻿using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CSharp;
using UdonSharp.Serialization;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using VRC.Udon.Common.Interfaces;

namespace UdonSharp.Compiler
{
    public class UdonSharpCompiler
    {
        public class CompileError
        {
            public MonoScript script;
            public string errorStr;
            public int lineIdx;
            public int charIdx;
        }

        public class CompileTaskResult
        {
            public UdonSharpProgramAsset programAsset;
            public string compiledAssembly;
            public uint symbolCount;
            public List<CompileError> compileErrors = new List<CompileError>();
        }

        private CompilationModule[] modules;
        private bool isEditorBuild = true;

        private static int initAssemblyCounter = 0;

        public UdonSharpCompiler(UdonSharpProgramAsset programAsset, bool editorBuild = true)
        {
            modules = new CompilationModule[] { new CompilationModule(programAsset) };
            isEditorBuild = editorBuild;
        }

        public UdonSharpCompiler(UdonSharpProgramAsset[] programAssets, bool editorBuild = true)
        {
            modules = programAssets.Where(e => e.sourceCsScript != null).Select(e => new CompilationModule(e)).ToArray();
            isEditorBuild = editorBuild;
        }

        public void Compile()
        {
            Profiler.BeginSample("UdonSharp Compile");

            System.Diagnostics.Stopwatch compileTimer = new System.Diagnostics.Stopwatch();
            compileTimer.Start();

            int totalErrorCount = 0;

            try
            {
                EditorUtility.DisplayProgressBar("UdonSharp Compile", "Building class definitions...", 0f);

                string defineString = UdonSharpUtils.GetProjectDefineString(isEditorBuild);

                // Bind stage
                List<ClassDefinition> classDefinitions = BindPrograms(defineString);

                if (classDefinitions == null)
                    totalErrorCount++;

                // Compile stage
                if (totalErrorCount == 0)
                {
#if UDONSHARP_DEBUG // Single threaded compile
                    List<CompileTaskResult> compileTasks = new List<CompileTaskResult>();

                    foreach (CompilationModule module in modules)
                    {
                        compileTasks.Add(module.Compile(classDefinitions, defineString, isEditorBuild));
                    }
#else
                    List<Task<CompileTaskResult>> compileTasks = new List<Task<CompileTaskResult>>();

                    foreach (CompilationModule module in modules)
                    {
                        compileTasks.Add(Task.Factory.StartNew(() => module.Compile(classDefinitions, defineString, isEditorBuild)));
                    }
#endif

                    int totalTaskCount = compileTasks.Count;

                    while (compileTasks.Count > 0)
                    {
#if UDONSHARP_DEBUG
                        CompileTaskResult compileResult = compileTasks.Last();
                        compileTasks.RemoveAt(compileTasks.Count - 1);
#else
                        Task<CompileTaskResult> compileResultTask = Task.WhenAny(compileTasks).Result;
                        compileTasks.Remove(compileResultTask);

                        CompileTaskResult compileResult = compileResultTask.Result;
#endif

                        if (compileResult.compileErrors.Count == 0)
                        {
                            compileResult.programAsset.SetUdonAssembly(compileResult.compiledAssembly);
                            bool assembled = compileResult.programAsset.AssembleCsProgram(compileResult.symbolCount);
                            compileResult.programAsset.SetUdonAssembly("");
                            UdonSharpEditorCache.Instance.SetUASMStr(compileResult.programAsset, compileResult.compiledAssembly);

                            if (!assembled)
                            {
                                FieldInfo assemblyError = typeof(VRC.Udon.Editor.ProgramSources.UdonAssemblyProgramAsset).GetField("assemblyError", BindingFlags.NonPublic | BindingFlags.Instance);
                                string error = (string)assemblyError.GetValue(compileResult.programAsset);

                                totalErrorCount++;

                                if (!string.IsNullOrEmpty(error))
                                    UdonSharpUtils.LogBuildError(error, AssetDatabase.GetAssetPath(compileResult.programAsset.sourceCsScript), 0, 0);
                                else
                                    UdonSharpUtils.LogBuildError("Failed to assemble program", AssetDatabase.GetAssetPath(compileResult.programAsset.sourceCsScript), 0, 0);
                            }
                        }
                        else
                        {
                            foreach (CompileError error in compileResult.compileErrors)
                            {
                                string errorMessage = UdonSharpUtils.LogBuildError(error.errorStr,
                                                                                   AssetDatabase.GetAssetPath(error.script).Replace("/", "\\"),
                                                                                   error.lineIdx,
                                                                                   error.charIdx);

                                compileResult.programAsset.compileErrors.Add(errorMessage);
                            }

                            totalErrorCount += compileResult.compileErrors.Count;
                        }

                        int processedTaskCount = totalTaskCount - compileTasks.Count;

                        EditorUtility.DisplayProgressBar("UdonSharp Compile",
                                                         $"Compiling scripts ({processedTaskCount}/{totalTaskCount})...",
                                                         Mathf.Clamp01((processedTaskCount / ((float)totalTaskCount + 1f))));
                    }

                    if (totalErrorCount == 0)
                    {
                        EditorUtility.DisplayProgressBar("UdonSharp Compile", "Assigning constants...", 1f);
                        int initializerErrorCount = AssignHeapConstants();
                        totalErrorCount += initializerErrorCount;
                    }

                    if (totalErrorCount == 0)
                    {
                        foreach (CompilationModule module in modules)
                        {
                            module.programAsset.ApplyProgram();
                            UdonSharpEditorCache.Instance.UpdateSourceHash(module.programAsset);
                        }

                        EditorUtility.DisplayProgressBar("UdonSharp Compile", "Post Build Scene Fixup", 1f);
                        UdonSharpEditorCache.Instance.LastBuildType = isEditorBuild ? UdonSharpEditorCache.DebugInfoType.Editor : UdonSharpEditorCache.DebugInfoType.Client;
                        UdonSharpEditorManager.RunPostBuildSceneFixup();
                    }
                }

                if (totalErrorCount > 0)
                {
                    foreach (CompilationModule module in modules)
                    {
                        UdonSharpEditorCache.Instance.ClearSourceHash(module.programAsset);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Profiler.EndSample();
            }

            compileTimer.Stop();

            if (totalErrorCount == 0)
            {
                if (modules.Length > 5)
                {
                    Debug.Log($"[<color=#0c824c>UdonSharp</color>] Compile of {modules.Length} scripts finished in {compileTimer.Elapsed.ToString("mm\\:ss\\.fff")}");
                }
                else
                {
                    Debug.Log($"[<color=#0c824c>UdonSharp</color>] Compile of script{(modules.Length > 1 ? "s" : "")} {string.Join(", ", modules.Select(e => Path.GetFileName(AssetDatabase.GetAssetPath(e.programAsset.sourceCsScript))))} finished in {compileTimer.Elapsed.ToString("mm\\:ss\\.fff")}");
                }
            }
        }

        List<ClassDefinition> BindPrograms(string defineStr)
        {
            List<BindTaskResult> bindTaskResults = new List<BindTaskResult>();

            UdonSharpProgramAsset[] allPrograms = UdonSharpProgramAsset.GetAllUdonSharpPrograms();

            List<ClassDefinitionBinder> classBinders = new List<ClassDefinitionBinder>();

            foreach (UdonSharpProgramAsset programAsset in allPrograms)
            {
                if (programAsset == null || programAsset.sourceCsScript == null)
                    continue;

                classBinders.Add(new ClassDefinitionBinder(programAsset));
            }

#if UDONSHARP_DEBUG // Single threaded bind
            List<BindTaskResult> bindTasks = new List<BindTaskResult>();

            foreach (ClassDefinitionBinder binder in classBinders)
            {
                bindTasks.Add(binder.BuildClassDefinition(defineStr));
            }
#else
            List<Task<BindTaskResult>> bindTasks = new List<Task<BindTaskResult>>();

            foreach (ClassDefinitionBinder binder in classBinders)
            {
                bindTasks.Add(Task.Factory.StartNew(() => binder.BuildClassDefinition(defineStr)));
            }
#endif

            int errorCount = 0;
            List<ClassDefinition> classDefinitions = new List<ClassDefinition>();

            while (bindTasks.Count > 0)
            {
#if UDONSHARP_DEBUG
                BindTaskResult bindResult = bindTasks.Last();
                bindTasks.RemoveAt(bindTasks.Count - 1);
#else
                Task<BindTaskResult> bindResultTask = Task.WhenAny(bindTasks).Result;
                bindTasks.Remove(bindResultTask);

                BindTaskResult bindResult = bindResultTask.Result;
#endif

                if (bindResult.compileErrors.Count == 0)
                {
                    classDefinitions.Add(bindResult.classDefinition);
                }
                else
                {
                    errorCount++;

                    foreach (CompileError bindError in bindResult.compileErrors)
                    {
                        UdonSharpUtils.LogBuildError(bindError.errorStr, AssetDatabase.GetAssetPath(bindResult.sourceScript), bindError.lineIdx, bindError.charIdx);
                    }
                }
            }

            if (errorCount == 0)
                return classDefinitions;

            return null;
        }

        public int AssignHeapConstants()
        {
            CompilationModule[] compiledModules = modules.Where(e => e.ErrorCount == 0).ToArray();

            foreach (CompilationModule module in compiledModules)
            {
                IUdonProgram program = module.programAsset.GetRealProgram();

                if (program != null)
                {
                    foreach (SymbolDefinition symbol in module.moduleSymbols.GetAllUniqueChildSymbols())
                    {
                        uint symbolAddress = program.SymbolTable.GetAddressFromSymbol(symbol.symbolUniqueName);

                        if (symbol.symbolDefaultValue != null)
                        {
                            program.Heap.SetHeapVariable(symbolAddress, symbol.symbolDefaultValue, symbol.symbolCsType);
                        }
                        else if (symbol.declarationType.HasFlag(SymbolDeclTypeFlags.Public)) // Initialize null array fields to a 0-length array like Unity does
                        {
                            if (symbol.symbolCsType.IsArray)
                                program.Heap.SetHeapVariable(symbolAddress, System.Activator.CreateInstance(symbol.symbolCsType, new object[] { 0 }), symbol.symbolCsType);
                            else if (symbol.symbolCsType == typeof(string))
                                program.Heap.SetHeapVariable(symbolAddress, "", symbol.symbolCsType);
                        }
                    }
                }
            }

            int fieldInitializerErrorCount = RunFieldInitalizers(compiledModules);

            foreach (CompilationModule module in compiledModules)
            {
                IUdonProgram program = module.programAsset.GetRealProgram();

                if (program != null)
                {
                    // Do not let users assign null to array fields, Unity does not allow this in its normal handling
                    foreach (SymbolDefinition symbol in module.moduleSymbols.GetAllUniqueChildSymbols())
                    {
                        uint symbolAddress = program.SymbolTable.GetAddressFromSymbol(symbol.symbolUniqueName);

                        if (symbol.declarationType.HasFlag(SymbolDeclTypeFlags.Public)) // Initialize null array fields to a 0-length array like Unity does
                        {
                            if (symbol.symbolCsType.IsArray)
                            {
                                if (program.Heap.GetHeapVariable(symbolAddress) == null)
                                {
                                    program.Heap.SetHeapVariable(symbolAddress, System.Activator.CreateInstance(symbol.symbolCsType, new object[] { 0 }), symbol.symbolCsType);
                                }
                            }
                            else if (symbol.symbolCsType == typeof(string))
                            {
                                if (program.Heap.GetHeapVariable(symbolAddress) == null)
                                {
                                    program.Heap.SetHeapVariable(symbolAddress, "", symbol.symbolCsType);
                                }
                            }
                        }

                        // Default to empty string on synced strings to prevent Udon sync from throwing errors
                        if (symbol.symbolCsType == typeof(string) &&
                            symbol.declarationType.HasFlag(SymbolDeclTypeFlags.Private) &&
                            symbol.syncMode != UdonSyncMode.NotSynced)
                        {
                            if (program.Heap.GetHeapVariable(symbolAddress) == null)
                                program.Heap.SetHeapVariable(symbolAddress, "");
                        }
                    }
                }
            }

            return fieldInitializerErrorCount;
        }
        
        // Called from the generated assembly to assign values to the program
        static void SetHeapField<T>(IUdonProgram program, T value, string symbolName)
        {
            if (UdonSharpUtils.IsUserJaggedArray(typeof(T)))
            {
                Serializer<T> serializer = Serializer.CreatePooled<T>();

                SimpleValueStorage<object[]> arrayStorage = new SimpleValueStorage<object[]>();
                serializer.Write(arrayStorage, in value);
                
                program.Heap.SetHeapVariable<object[]>(program.SymbolTable.GetAddressFromSymbol(symbolName), arrayStorage.Value);
            }
            else
            {
                //program.Heap.SetHeapVariable<T>(program.SymbolTable.GetAddressFromSymbol(symbolName), value);
                throw new System.NotImplementedException(); // This should not get hit currently
            }
        }

        string GetFullTypeQualifiedName(System.Type type)
        {
            string namespaceStr = "";

            if (type.Namespace != null &&
                type.Namespace.Length > 0)
            {
                namespaceStr = type.Namespace + ".";
            }

            string nestedTypeStr = "";

            System.Type declaringType = type.DeclaringType;

            while (declaringType != null)
            {
                nestedTypeStr = $"{declaringType.Name}.{nestedTypeStr}";
                declaringType = declaringType.DeclaringType;
            }

            return namespaceStr + nestedTypeStr + type.Name;
        }

        private int RunFieldInitalizers(CompilationModule[] compiledModules)
        {
            CompilationModule[] modulesToInitialize = compiledModules.Where(e => e.fieldsWithInitializers.Count > 0).ToArray();
            
            // We don't need to run the costly compilation if the user hasn't defined any fields with initializers
            if (modulesToInitialize.Length == 0)
                return 0;

            int initializerErrorCount = 0;

            Microsoft.CodeAnalysis.SyntaxTree[] initializerTrees = new Microsoft.CodeAnalysis.SyntaxTree[modulesToInitialize.Length];
            StringBuilder[] codeStringBuilders = new StringBuilder[modulesToInitialize.Length];

            for (int moduleIdx = 0; moduleIdx < modulesToInitialize.Length; ++moduleIdx)
            {
                CompilationModule module = modulesToInitialize[moduleIdx];

                CodeCompileUnit compileUnit = new CodeCompileUnit();
                CodeNamespace ns = new CodeNamespace("FieldInitialzers");
                compileUnit.Namespaces.Add(ns);
                foreach (var resolverUsingNamespace in module.resolver.usingNamespaces)
                {
                    if (!string.IsNullOrEmpty(resolverUsingNamespace))
                        ns.Imports.Add(new CodeNamespaceImport(resolverUsingNamespace));
                }

                CodeTypeDeclaration _class = new CodeTypeDeclaration($"Initializer{moduleIdx}");
                ns.Types.Add(_class);
                CodeMemberMethod method = new CodeMemberMethod();
                _class.Members.Add(method);
                method.Attributes = MemberAttributes.Public | MemberAttributes.Static;
                method.ReturnType = new CodeTypeReference(typeof(void));
                method.Name = "DoInit";
                method.Parameters.Add(new CodeParameterDeclarationExpression(typeof(IUdonProgram), "program"));
                method.Parameters.Add(new CodeParameterDeclarationExpression(typeof(MethodInfo), "heapSetMethod"));

                foreach (var fieldDeclarationSyntax in module.fieldsWithInitializers)
                {
                    var type = fieldDeclarationSyntax.Declaration.Type;
                    int count = 0;
                    bool isConst = fieldDeclarationSyntax.Modifiers.Any(t => t.ToString() == "const");
                    foreach (var variable in fieldDeclarationSyntax.Declaration.Variables)
                    {
                        FieldDefinition fieldDef = module.compiledClassDefinition?.fieldDefinitions?.Find(e => (e.fieldSymbol.declarationType == SymbolDeclTypeFlags.Private || e.fieldSymbol.declarationType == SymbolDeclTypeFlags.Public) &&
                                                                                                                e.fieldSymbol.symbolOriginalName == variable.Identifier.ToString());

                        string typeQualifiedName = type.ToString().Replace('+', '.');
                        if (fieldDef != null)
                            typeQualifiedName = GetFullTypeQualifiedName(fieldDef.fieldSymbol.symbolCsType);

                        if (variable.Initializer != null)
                        {
                            string name = variable.Identifier.ToString();
                            if (isConst)
                            {
                                _class.Members.Add(new CodeSnippetTypeMember($"const {typeQualifiedName} {name} {variable.Initializer};"));
                            }
                            else
                            {
                                method.Statements.Add(new CodeSnippetStatement($"{typeQualifiedName} {name} {variable.Initializer};"));
                            }

                            if (UdonSharpUtils.IsUserJaggedArray(fieldDef.fieldSymbol.userCsType))
                            {
                                method.Statements.Add(new CodeSnippetStatement(
                                    "heapSetMethod.MakeGenericMethod(typeof(" + GetFullTypeQualifiedName(fieldDef.fieldSymbol.userCsType) + ")).Invoke(null, new object[] { program, " + name + ", \"" + variable.Identifier + "\"});"));
                            }
                            else
                            {
                                method.Statements.Add(new CodeSnippetStatement(
                                    $"program.Heap.SetHeapVariable(program.SymbolTable.GetAddressFromSymbol(\"{variable.Identifier}\"), {name});"));
                            }

                            count++;
                        }
                    }
                }

                StringBuilder sb = new StringBuilder();
                codeStringBuilders[moduleIdx] = sb;

                CSharpCodeProvider provider = new CSharpCodeProvider();
                using (StringWriter streamWriter = new StringWriter(sb))
                {
                    provider.GenerateCodeFromCompileUnit(compileUnit, streamWriter, new CodeGeneratorOptions());
                }

                Microsoft.CodeAnalysis.SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(sb.ToString());

                initializerTrees[moduleIdx] = syntaxTree;
            }

            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            var references = new List<MetadataReference>();
            for (int i = 0; i < assemblies.Length; i++)
            {
                if (!assemblies[i].IsDynamic && assemblies[i].Location.Length > 0)
                    references.Add(MetadataReference.CreateFromFile(assemblies[i].Location));
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                $"init{initAssemblyCounter++}",
                syntaxTrees: initializerTrees,
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var memoryStream = new MemoryStream())
            {
                EmitResult result = compilation.Emit(memoryStream);
                if (!result.Success)
                {
                    // todo: make these errors point to the correct source files
                    bool error = false;
                    foreach (Diagnostic diagnostic in result.Diagnostics)
                    {
                        if (diagnostic.Severity == DiagnosticSeverity.Error)
                        {
                            Debug.LogError(diagnostic);
                            error = true;
                            initializerErrorCount++;
                        }
                    }

                    if (error)
                        Debug.LogError($"Generated Source code: {string.Join("\n", codeStringBuilders.Select(e => e.ToString()))}");
                }
                else
                {
                    memoryStream.Seek(0, SeekOrigin.Begin);

                    Assembly assembly = Assembly.Load(memoryStream.ToArray());

                    for (int moduleIdx = 0; moduleIdx < modulesToInitialize.Length; ++moduleIdx)
                    {
                        CompilationModule module = modulesToInitialize[moduleIdx];
                        IUdonProgram program = module.programAsset.GetRealProgram();

                        System.Type cls = assembly.GetType($"FieldInitialzers.Initializer{moduleIdx}");
                        MethodInfo methodInfo = cls.GetMethod("DoInit", BindingFlags.Public | BindingFlags.Static);
                        methodInfo.Invoke(null, new object[] { program, typeof(UdonSharpCompiler).GetMethod("SetHeapField", BindingFlags.NonPublic | BindingFlags.Static) });

                        foreach (var fieldDeclarationSyntax in module.fieldsWithInitializers)
                        {
                            foreach (var variable in fieldDeclarationSyntax.Declaration.Variables)
                            {
                                string varName = variable.Identifier.ToString();

                                object heapValue = program.Heap.GetHeapVariable(program.SymbolTable.GetAddressFromSymbol(varName));

                                if (heapValue != null && UdonSharpUtils.IsUserDefinedType(heapValue.GetType()))
                                {
                                    string fieldError = $"Field: '{varName}' UdonSharp does not yet support field initializers on user-defined types";

                                    UdonSharpUtils.LogBuildError(fieldError, AssetDatabase.GetAssetPath(module.programAsset.sourceCsScript), 0, 0);

                                    module.programAsset.compileErrors.Add(fieldError);
                                }
                            }
                        }
                    }
                }
            }

            return initializerErrorCount;
        }

        class BindTaskResult
        {
            public ClassDefinition classDefinition;
            public MonoScript sourceScript;
            public List<CompileError> compileErrors = new List<CompileError>();
        }

        class ClassDefinitionBinder
        {
            UdonSharpProgramAsset programAsset;
            string sourceFilePath;

            public ClassDefinitionBinder(UdonSharpProgramAsset programAsset)
            {
                this.programAsset = programAsset;
                sourceFilePath = AssetDatabase.GetAssetPath(programAsset.sourceCsScript);
            }

            public BindTaskResult BuildClassDefinition(string defineString)
            {
                string programSource = defineString + UdonSharpUtils.ReadFileTextSync(sourceFilePath);

                ResolverContext resolver = new ResolverContext();
                SymbolTable classSymbols = new SymbolTable(resolver, null);

                classSymbols.OpenSymbolTable();

                LabelTable classLabels = new LabelTable();

                Microsoft.CodeAnalysis.SyntaxTree tree = CSharpSyntaxTree.ParseText(programSource);

                ClassVisitor classVisitor = new ClassVisitor(resolver, classSymbols, classLabels);

                BindTaskResult bindTaskResult = new BindTaskResult();
                bindTaskResult.sourceScript = programAsset.sourceCsScript;

                try
                {
                    classVisitor.Visit(tree.GetRoot());
                }
                catch (System.Exception e)
                {
                    SyntaxNode node = classVisitor.visitorContext.currentNode;

                    string errorString;
                    int charIndex;
                    int lineIndex;

                    if (node != null)
                    {
                        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();
                        
                        charIndex = lineSpan.StartLinePosition.Character;
                        lineIndex = lineSpan.StartLinePosition.Line - defineString.Count(c => c == '\n');
                    }
                    else
                    {
                        charIndex = 0;
                        lineIndex = 0;
                    }

                    errorString = $"{e.GetType()}: {e.Message}";

#if UDONSHARP_DEBUG
                    Debug.LogException(e);
                    Debug.LogError(e.StackTrace);
#endif
                    
                    bindTaskResult.compileErrors.Add(new CompileError() { script = programAsset.sourceCsScript, errorStr = errorString, charIdx = charIndex, lineIdx = lineIndex });

                    classSymbols.CloseSymbolTable();

                    return bindTaskResult;
                }

                classSymbols.CloseSymbolTable();

                int errorCount = 0;

                foreach (Diagnostic diagnostic in tree.GetDiagnostics())
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        errorCount++;

                        LinePosition linePosition = diagnostic.Location.GetLineSpan().StartLinePosition;

                        CompileError error = new CompileError();
                        error.script = programAsset.sourceCsScript;
                        error.errorStr = $"error {diagnostic.Descriptor.Id}: {diagnostic.GetMessage()}";
                        error.lineIdx = linePosition.Line - defineString.Count(c => c == '\n');
                        error.charIdx = linePosition.Character;

                        bindTaskResult.compileErrors.Add(error);
                    }
                }

                if (errorCount > 0)
                {
                    return bindTaskResult;
                }

                classVisitor.classDefinition.classScript = programAsset.sourceCsScript;

                bindTaskResult.classDefinition = classVisitor.classDefinition;

                return bindTaskResult;
            }
        }
    }
}