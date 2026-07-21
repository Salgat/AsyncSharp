using BenchmarkDotNet.Running;

namespace AsyncSharp.Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
    {
        var run = BenchmarkRun.Parse(args);
        Environment.CurrentDirectory = run.RepositoryRoot;
        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(run.BenchmarkDotNetArguments, run.CreateConfig());
    }
}
