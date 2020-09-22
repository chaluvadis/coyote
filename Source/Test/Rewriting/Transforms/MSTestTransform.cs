﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Coyote.IO;
using Microsoft.Coyote.SystematicTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Microsoft.Coyote.Rewriting
{
    internal class MSTestTransform : AssemblyTransform
    {
        /// <summary>
        /// The test configuration to use when rewriting unit tests.
        /// </summary>
        private readonly Configuration Configuration;

        /// <summary>
        /// The current module being transformed.
        /// </summary>
        private ModuleDefinition Module;

        /// <summary>
        /// Initializes a new instance of the <see cref="MSTestTransform"/> class.
        /// </summary>
        internal MSTestTransform(Configuration configuration, ConsoleLogger log)
            : base(log)
        {
            this.Configuration = configuration;
        }

        /// <inheritdoc/>
        internal override void VisitModule(ModuleDefinition module)
        {
            this.Module = module;
        }

        /// <inheritdoc/>
        internal override void VisitMethod(MethodDefinition method)
        {
            if (method.IsAbstract)
            {
                return;
            }

            bool isTestMethod = false;
            if (method.CustomAttributes.Count > 0)
            {
                // Search for a method with a unit testing framework attribute.
                foreach (var attr in method.CustomAttributes)
                {
                    if (attr.AttributeType.FullName == "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute")
                    {
                        isTestMethod = true;
                        break;
                    }
                }
            }

            if (isTestMethod)
            {
                Debug.WriteLine($"............. [-] test method '{method.Name}'");

                MethodDefinition newMethod = this.CloneMethod(method);
                this.RewriteTestMethod(method, newMethod);

                method.DeclaringType.Methods.Add(newMethod);

                Debug.WriteLine($"............. [+] systematic test method '{method.Name}'");
                Debug.WriteLine($"............. [+] test method '{newMethod.Name}'");
            }
        }

        /// <summary>
        /// Clones the test method.
        /// </summary>
        internal MethodDefinition CloneMethod(MethodDefinition method)
        {
            int index = 1;
            string newName = $"{method.Name}_{index}";
            while (method.DeclaringType.Methods.Any(m => m.Name == newName))
            {
                index++;
                newName = $"{method.Name}_{index}";
            }

            MethodDefinition newMethod = new MethodDefinition(newName, method.Attributes, method.ReturnType);
            newMethod.Body.InitLocals = method.Body.InitLocals;

            foreach (var variable in method.Body.Variables)
            {
                newMethod.Body.Variables.Add(variable);
            }

            foreach (var instruction in method.Body.Instructions)
            {
                newMethod.Body.Instructions.Add(instruction);
            }

            foreach (var handler in method.Body.ExceptionHandlers)
            {
                newMethod.Body.ExceptionHandlers.Add(handler);
            }

            newMethod.Body.OptimizeMacros();

            return newMethod;
        }

        /// <summary>
        /// Creates a new method for invoking the original test method from the testing engine.
        /// </summary>
        internal void RewriteTestMethod(MethodDefinition method, MethodDefinition testMethod)
        {
            bool isAsyncMethod = false;
            TypeReference asyncReturnType = null;
            // move any AsyncStateMachineAttributes.
            foreach (var attr in method.CustomAttributes.ToArray())
            {
                var typeName = attr.AttributeType.FullName;
                if (typeName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")
                {
                    isAsyncMethod = true;
                    asyncReturnType = method.ReturnType;
                    method.ReturnType = this.Module.ImportReference(typeof(void));
                }

                if (typeName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute" ||
                    typeName == "System.Diagnostics.DebuggerStepThroughAttribute")
                {
                    method.CustomAttributes.Remove(attr);
                    testMethod.CustomAttributes.Add(attr);
                }
            }

            method.Body.Variables.Clear();
            method.Body.Instructions.Clear();
            method.Body.ExceptionHandlers.Clear();

            MethodReference launchMethod = null;
            if (this.Configuration.AttachDebugger)
            {
                var debuggerType = this.Module.ImportReference(typeof(System.Diagnostics.Debugger)).Resolve();
                launchMethod = FindMatchingMethod(debuggerType, "Launch");
            }

            TypeReference actionType;
            if (isAsyncMethod)
            {
                var funcType = this.Module.ImportReference(typeof(Func<>));
                actionType = ImportGenericTypeInstance(this.Module, funcType, asyncReturnType);
            }
            else
            {
                actionType = this.Module.ImportReference(typeof(Action));
            }

            var configurationType = this.Module.ImportReference(typeof(Configuration));
            var engineType = this.Module.ImportReference(typeof(TestingEngine));

            var resolvedActionType = actionType.Resolve(); // Func<>
            var resolvedConfigurationType = configurationType.Resolve();
            var resolvedEngineType = engineType.Resolve();

            MethodReference actionConstructor = this.Module.ImportReference(
                resolvedActionType.Methods.FirstOrDefault(m => m.IsConstructor));
            if (isAsyncMethod)
            {
                actionConstructor = ImportGenericMethodInstance(this.Module, actionConstructor, asyncReturnType);
            }

            MethodReference createConfigurationMethod = this.Module.ImportReference(
                resolvedConfigurationType.Methods.FirstOrDefault(m => m.Name is "Create"));
            MethodReference createEngineMethod = this.Module.ImportReference(
                FindMatchingMethod(resolvedEngineType, "Create", configurationType, actionType));

            // The emitted IL corresponds to a method body such as:
            //   Configuration configuration = Configuration.Create();
            //   TestingEngine engine = TestingEngine.Create(configuration, new Action(Test));
            //   engine.Run();
            //   engine.RethrowUnhandledException();
            //
            // With optional calls to setup some of the configuration options based on Coyote command line:
            // including:
            //   configuration.WithTestingIterations(n);
            //   configuration.WithMaxSchedulingSteps(x, y);
            //   configuration.WithProbabilisticStrategy(x);
            //   configuration.WithPCTStrategy(x);
            //   configuration.SchedulingStrategy(x);
            //   configuration.WithLivenessTemperatureThreshold(x);
            //   configuration.WithTimeoutDelay(x);
            //   configuration.WithRandomGeneratorSeed(x);
            //   configuration.WithVerbosityEnabled(x);
            //   configuration.WithTelemetryEnabled(x);
            var processor = method.Body.GetILProcessor();
            if (launchMethod != null)
            {
                processor.Emit(OpCodes.Call, this.Module.ImportReference(launchMethod));
                processor.Emit(OpCodes.Pop);
            }

            processor.Emit(OpCodes.Call, createConfigurationMethod);
            if (this.Configuration.TestingIterations != 1)
            {
                this.EmitMethodCall(processor, resolvedConfigurationType, "WithTestingIterations", this.Configuration.TestingIterations);
            }

            if (this.Configuration.MaxUnfairSchedulingSteps != 10000 || this.Configuration.MaxFairSchedulingSteps != 100000)
            {
                this.EmitMethodCall(processor, resolvedConfigurationType, "WithMaxSchedulingSteps", this.Configuration.MaxUnfairSchedulingSteps, this.Configuration.MaxFairSchedulingSteps);
            }

            if (this.Configuration.SchedulingStrategy != "random")
            {
                switch (this.Configuration.SchedulingStrategy)
                {
                    case "fairpct":
                        this.EmitMethodCall(processor, resolvedConfigurationType, "WithProbabilisticStrategy", this.Configuration.StrategyBound);
                        break;
                    case "pct":
                        this.EmitMethodCall(processor, resolvedConfigurationType, "WithPCTStrategy", false, this.Configuration.StrategyBound);
                        break;
                    case "dfs":
                        this.EmitMethodCall(processor, resolvedConfigurationType, "SchedulingStrategy", this.Configuration.ScheduleTrace);
                        break;
                    default:
                        break;
                }
            }

            if (this.Configuration.UserExplicitlySetLivenessTemperatureThreshold)
            {
                this.EmitMethodCall(processor, resolvedConfigurationType, "WithLivenessTemperatureThreshold", this.Configuration.LivenessTemperatureThreshold);
            }

            if (this.Configuration.TimeoutDelay != 10)
            {
                this.EmitMethodCall(processor, resolvedConfigurationType, "WithTimeoutDelay", this.Configuration.TimeoutDelay);
            }

            if (this.Configuration.RandomGeneratorSeed.HasValue)
            {
                this.EmitMethodCall(processor, resolvedConfigurationType, "WithRandomGeneratorSeed", this.Configuration.RandomGeneratorSeed.Value);
            }

            if (this.Configuration.IsVerbose)
            {
                this.EmitMethodCall(processor, resolvedConfigurationType, "WithVerbosityEnabled", this.Configuration.IsVerbose);
            }

            if (!this.Configuration.EnableTelemetry)
            {
                this.EmitMethodCall(processor, resolvedConfigurationType, "WithTelemetryEnabled", this.Configuration.EnableTelemetry);
            }

            processor.Emit(OpCodes.Ldarg_0);
            processor.Emit(OpCodes.Ldftn, testMethod);
            processor.Emit(OpCodes.Newobj, actionConstructor);
            processor.Emit(OpCodes.Call, createEngineMethod);
            processor.Emit(OpCodes.Dup);
            this.EmitMethodCall(processor, resolvedEngineType, "Run");
            this.EmitMethodCall(processor, resolvedEngineType, "RethrowUnhandledException");
            processor.Emit(OpCodes.Ret);

            method.Body.OptimizeMacros();
        }

        private void EmitMethodCall(ILProcessor processor, TypeDefinition typedef, string methodName, params object[] arguments)
        {
            List<TypeReference> typeRefs = new List<TypeReference>();
            foreach (var arg in arguments)
            {
                Type t = arg.GetType();
                if (t == typeof(bool))
                {
                    int i = ((bool)arg) ? 1 : 0;
                    processor.Emit(OpCodes.Ldc_I4, i);
                }
                else if (t == typeof(int))
                {
                    var i = (int)arg;
                    processor.Emit(OpCodes.Ldc_I4, i);
                }
                else if (t == typeof(uint))
                {
                    var i = (int)(uint)arg;
                    processor.Emit(OpCodes.Ldc_I4, i);
                }
                else
                {
                    throw new Exception(string.Format("Argument type '{0}' is not supported", t.FullName));
                }

                typeRefs.Add(this.Module.ImportReference(t));
            }

            var method = FindMatchingMethod(typedef, methodName, typeRefs.ToArray());
            if (method == null)
            {
                throw new Exception(string.Format("Internal error looking for method '{0}' on type '{1}'", methodName, typedef.FullName));
            }

            processor.Emit(OpCodes.Call, this.Module.ImportReference(method));
        }
    }
}
