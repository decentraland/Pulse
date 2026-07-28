using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using System.Reflection;

// InProcessEmitToolchain bypasses DotNetSdkValidator which crashes on .NET 10
ManualConfig config = DefaultConfig.Instance
                                   .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance));

// Every [Benchmark] class in the assembly is selectable, so a suite can be chosen without editing
// this file. With no arguments BenchmarkDotNet lists the classes and prompts for one.
//
//   dotnet run -c Release --project src/DCLPulseBenchmarks -- --list flat
//   dotnet run -c Release --project src/DCLPulseBenchmarks -- --filter *ClusterTracker*
//   dotnet run -c Release --project src/DCLPulseBenchmarks -- --filter *ClusterTrackerBenchmarks.Pass*
BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args, config);
