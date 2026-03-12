using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using DCLPulseBenchmarks;

// InProcessEmitToolchain bypasses DotNetSdkValidator which crashes on .NET 10
ManualConfig config = DefaultConfig.Instance
                                   .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance));

BenchmarkRunner.Run<SpatialInterestBenchmarks>(config, args);
