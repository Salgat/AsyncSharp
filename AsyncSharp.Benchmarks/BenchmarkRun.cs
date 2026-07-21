using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using Perfolizer.Horology;

namespace AsyncSharp.Benchmarks;

internal sealed class BenchmarkRun
{
    private const string ProfileOption = "--profile";
    private const string RunIdOption = "--run-id";
    private const int FullIterationTimeMilliseconds = 100;
    private const int ShortIterationTimeMilliseconds = 25;
    private const int TieredCompilationWarmupCount = 16;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private BenchmarkRun(
        BenchmarkProfile profile,
        string? runLabel,
        string repositoryRoot,
        string[] benchmarkDotNetArguments,
        DateTimeOffset startedUtc,
        string commit,
        bool? sourceWorkingTreeDirty,
        string sourceSnapshotSha256)
    {
        Profile = profile;
        RunLabel = runLabel;
        RepositoryRoot = repositoryRoot;
        BenchmarkDotNetArguments = benchmarkDotNetArguments;
        StartedUtc = startedUtc;
        Commit = commit;
        ShortCommit = commit == "unknown" ? commit : commit[..Math.Min(12, commit.Length)];
        SourceWorkingTreeDirty = sourceWorkingTreeDirty;
        SourceSnapshotSha256 = sourceSnapshotSha256;

        var directoryName = string.Create(
            CultureInfo.InvariantCulture,
            $"{StartedUtc:yyyyMMdd-HHmmssfff}-{ShortCommit}-{Profile.ToString().ToLowerInvariant()}");
        RunId = RunLabel is null ? directoryName : $"{directoryName}-{RunLabel}";
    }

    public BenchmarkProfile Profile { get; }

    public string? RunLabel { get; }

    public string RunId { get; }

    public string RepositoryRoot { get; }

    public string[] BenchmarkDotNetArguments { get; }

    public DateTimeOffset StartedUtc { get; }

    public string Commit { get; }

    public string ShortCommit { get; }

    public bool? SourceWorkingTreeDirty { get; }

    public string SourceSnapshotSha256 { get; }

    public static BenchmarkRun Parse(string[] args)
    {
        var profile = BenchmarkProfile.Full;
        string? runLabel = null;
        var benchmarkDotNetArguments = new List<string>(args.Length);

        for (var index = 0; index < args.Length; ++index)
        {
            var argument = args[index];
            if (TryReadOption(argument, ProfileOption, out var inlineProfile))
            {
                var value = inlineProfile ?? ReadFollowingValue(args, ref index, ProfileOption);
                if (!Enum.TryParse(value, ignoreCase: true, out profile))
                {
                    throw new ArgumentException(
                        $"Unknown benchmark profile '{value}'. Expected Full, Short, or Dry.",
                        nameof(args));
                }

                continue;
            }

            if (TryReadOption(argument, RunIdOption, out var inlineRunId))
            {
                runLabel = inlineRunId ?? ReadFollowingValue(args, ref index, RunIdOption);
                continue;
            }

            if (string.Equals(argument, "--job", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--job=", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Use --profile Full, Short, or Dry instead of BenchmarkDotNet's --job option.",
                    nameof(args));
            }

            benchmarkDotNetArguments.Add(argument);
        }

        if (!HasBenchmarkSelection(benchmarkDotNetArguments))
        {
            benchmarkDotNetArguments.Add("--filter");
            benchmarkDotNetArguments.Add("*");
        }

        if (runLabel is not null)
        {
            ValidateRunLabel(runLabel);
        }

        var repositoryRoot = FindRepositoryRoot();
        var commit = TryRunProcess(
                "git",
                ["rev-parse", "HEAD"],
                repositoryRoot)
            ?? "unknown";
        var sourceStatus = TryRunProcess(
            "git",
            [
                "status",
                "--porcelain=v1",
                "--untracked-files=all",
                "--",
                "AsyncSharp",
                "AsyncSharp.Benchmarks",
            ],
            repositoryRoot);

        return new BenchmarkRun(
            profile,
            runLabel,
            repositoryRoot,
            benchmarkDotNetArguments.ToArray(),
            DateTimeOffset.UtcNow,
            commit,
            sourceStatus is null ? null : sourceStatus.Length != 0,
            ComputeSourceSnapshotSha256(repositoryRoot));
    }

    public IConfig CreateConfig()
    {
        var artifactsPath = Path.Combine(
            RepositoryRoot,
            "BenchmarkDotNet.Artifacts",
            "runs",
            RunId);

        if (Directory.Exists(artifactsPath) || File.Exists(artifactsPath))
        {
            throw new InvalidOperationException(
                $"Benchmark artifact path already exists: '{artifactsPath}'. "
                + "Wait for a new timestamp before retrying; earlier runs are never overwritten.");
        }

        var job = CreateJob(Profile);
        Directory.CreateDirectory(artifactsPath);
        WriteManifest(artifactsPath);

        return ManualConfig
            .Create(DefaultConfig.Instance)
            .AddJob(job)
            .WithArtifactsPath(artifactsPath);
    }

    private void WriteManifest(string artifactsPath)
    {
        var manifest = new RunManifest(
            RunId,
            RunLabel,
            Profile.ToString(),
            StartedUtc,
            Commit,
            ShortCommit,
            SourceWorkingTreeDirty,
            SourceSnapshotSha256,
            "AsyncSharp/** and AsyncSharp.Benchmarks/**, excluding bin/ and obj/",
            RuntimeInformation.FrameworkDescription,
            Environment.Version.ToString(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            GetCpuDescription(),
            Environment.ProcessorCount,
            GetProcessAffinity(),
            PowerPlan.HighPerformance.ToString(),
            GetWindowsPowerScheme(),
            Environment.MachineName,
            Environment.ProcessId,
            typeof(Job).Assembly.GetName().Version?.ToString() ?? "unknown",
            GetJobSettings(Profile),
            BenchmarkDotNetArguments);

        var manifestPath = Path.Combine(artifactsPath, "run-manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestJsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static Job CreateJob(BenchmarkProfile profile)
        => profile switch
        {
            BenchmarkProfile.Full => new Job("Full")
                .WithStrategy(RunStrategy.Throughput)
                .WithLaunchCount(3)
                .WithWarmupCount(TieredCompilationWarmupCount)
                .WithIterationCount(5)
                .WithIterationTime(TimeInterval.FromMilliseconds(FullIterationTimeMilliseconds))
                .WithMinIterationTime(TimeInterval.FromMilliseconds(FullIterationTimeMilliseconds))
                .WithUnrollFactor(1)
                .WithPowerPlan(PowerPlan.HighPerformance),
            BenchmarkProfile.Short => new Job("Short")
                .WithStrategy(RunStrategy.Throughput)
                .WithLaunchCount(1)
                .WithWarmupCount(TieredCompilationWarmupCount)
                .WithIterationCount(3)
                .WithIterationTime(TimeInterval.FromMilliseconds(ShortIterationTimeMilliseconds))
                .WithMinIterationTime(TimeInterval.FromMilliseconds(5))
                .WithUnrollFactor(1)
                .WithPowerPlan(PowerPlan.HighPerformance),
            BenchmarkProfile.Dry => Job.Dry
                .WithId("Dry")
                .WithPowerPlan(PowerPlan.HighPerformance),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };

    private static JobSettings GetJobSettings(BenchmarkProfile profile)
        => profile switch
        {
            BenchmarkProfile.Full => new JobSettings(
                "Full",
                RunStrategy.Throughput.ToString(),
                LaunchCount: 3,
                WarmupCount: TieredCompilationWarmupCount,
                IterationCount: 5,
                IterationTimeMilliseconds: FullIterationTimeMilliseconds,
                MinIterationTimeMilliseconds: FullIterationTimeMilliseconds,
                InvocationCount: null,
                UnrollFactor: 1),
            BenchmarkProfile.Short => new JobSettings(
                "Short",
                RunStrategy.Throughput.ToString(),
                LaunchCount: 1,
                WarmupCount: TieredCompilationWarmupCount,
                IterationCount: 3,
                IterationTimeMilliseconds: ShortIterationTimeMilliseconds,
                MinIterationTimeMilliseconds: 5,
                InvocationCount: null,
                UnrollFactor: 1),
            BenchmarkProfile.Dry => new JobSettings(
                "Dry",
                RunStrategy.ColdStart.ToString(),
                LaunchCount: 1,
                WarmupCount: 1,
                IterationCount: 1,
                IterationTimeMilliseconds: null,
                MinIterationTimeMilliseconds: null,
                InvocationCount: 1,
                UnrollFactor: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };

    private static bool HasBenchmarkSelection(IEnumerable<string> arguments)
        => arguments.Any(argument =>
            string.Equals(argument, "--filter", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "-f", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "--list", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--list=", StringComparison.OrdinalIgnoreCase));

    private static bool TryReadOption(string argument, string option, out string? inlineValue)
    {
        if (string.Equals(argument, option, StringComparison.OrdinalIgnoreCase))
        {
            inlineValue = null;
            return true;
        }

        var prefix = option + "=";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            inlineValue = argument[prefix.Length..];
            if (inlineValue.Length == 0)
            {
                throw new ArgumentException($"{option} requires a value.", nameof(argument));
            }

            return true;
        }

        inlineValue = null;
        return false;
    }

    private static string ReadFollowingValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} requires a value.", nameof(args));
        }

        return args[index];
    }

    private static void ValidateRunLabel(string runLabel)
    {
        if (string.IsNullOrWhiteSpace(runLabel)
            || runLabel is "." or ".."
            || runLabel.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || runLabel.Contains(Path.DirectorySeparatorChar)
            || runLabel.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                $"Run id '{runLabel}' is not a valid directory-name suffix.",
                nameof(runLabel));
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var startingPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startingPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AsyncSharp.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the AsyncSharp repository root containing AsyncSharp.sln.");
    }

    private static string ComputeSourceSnapshotSha256(string repositoryRoot)
    {
        var sourceFiles = new[]
            {
                Path.Combine(repositoryRoot, "AsyncSharp"),
                Path.Combine(repositoryRoot, "AsyncSharp.Benchmarks"),
            }
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            .Select(path => new
            {
                FullPath = path,
                RelativePath = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
            })
            .Where(file => !HasGeneratedPathSegment(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in sourceFiles)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(file.FullPath));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool HasGeneratedPathSegment(string relativePath)
        => relativePath
            .Split('/')
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static string GetCpuDescription()
    {
        var processorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return string.IsNullOrWhiteSpace(processorIdentifier)
            ? "unknown"
            : processorIdentifier.Trim();
    }

    private static string GetProcessAffinity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return "unknown";
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"0x{process.ProcessorAffinity.ToInt64():X}");
        }
        catch (Exception exception) when (
            exception is PlatformNotSupportedException
                or NotSupportedException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    private string GetWindowsPowerScheme()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "unknown";
        }

        return TryRunProcess(
                "powercfg.exe",
                ["/getactivescheme"],
                RepositoryRoot)
            ?? "unknown";
    }

    private static string? TryRunProcess(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
            return process.ExitCode == 0 ? standardOutput.Result.Trim() : null;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
                or InvalidOperationException
                or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record RunManifest(
        string RunDirectory,
        string? RunLabel,
        string Profile,
        DateTimeOffset StartedUtc,
        string HeadCommit,
        string ShortCommit,
        bool? SourceWorkingTreeDirty,
        string SourceSnapshotSha256,
        string SourceSnapshotScope,
        string Runtime,
        string RuntimeVersion,
        string OperatingSystem,
        string ProcessArchitecture,
        string Cpu,
        int LogicalProcessorCount,
        string ProcessAffinity,
        string ConfiguredPowerPlan,
        string HostActivePowerSchemeBeforeRun,
        string MachineName,
        int ProcessId,
        string BenchmarkDotNetVersion,
        JobSettings Job,
        string[] BenchmarkDotNetArguments);

    private sealed record JobSettings(
        string Id,
        string RunStrategy,
        int LaunchCount,
        int WarmupCount,
        int IterationCount,
        int? IterationTimeMilliseconds,
        int? MinIterationTimeMilliseconds,
        int? InvocationCount,
        int? UnrollFactor);
}

internal enum BenchmarkProfile
{
    Full,
    Short,
    Dry,
}
