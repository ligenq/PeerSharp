using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace PeerSharp.EndToEnd;

internal sealed class Toolchain(BenchmarkOptions options)
{
    private const string BoostVersion = "1.88.0";
    private const string BoostDirectoryName = "boost_1_88_0";
    private const string BoostArchiveSha256 = "8ee21476f1aca1978339f0f4a218b9b8a6746eec83070f32630f97b09c7e91b7";
    private const string BoostArchiveUrl = "https://archives.boost.io/release/1.88.0/source/boost_1_88_0.zip";

    public async Task<ToolPaths> BuildAsync(CancellationToken cancellationToken)
    {
        DoctorResult doctor = Inspect();
        doctor.Print(Console.Out);
        if (!doctor.CanBuild) throw new InvalidOperationException("Build prerequisites are missing; see doctor output above.");

        string boostRoot = await EnsureBoostAsync(cancellationToken).ConfigureAwait(false);
        string peerSharpCli = await BuildPeerSharpAsync(doctor.Dotnet!, cancellationToken).ConfigureAwait(false);
        await EnsureLibtorrentBuildSubmoduleAsync(doctor.Git!, cancellationToken).ConfigureAwait(false);
        (string clientTest, string connectionTester, string buildRoot) =
            await BuildLibtorrentAsync(doctor.Cmake!, boostRoot, cancellationToken).ConfigureAwait(false);

        return new ToolPaths(
            doctor.Dotnet!, doctor.Cmake!, peerSharpCli, clientTest, connectionTester, boostRoot, buildRoot);
    }

    public ToolPaths FindBuiltTools()
    {
        DoctorResult doctor = Inspect();
        string boostRoot = Path.Combine(options.ArtifactRoot, "deps", BoostDirectoryName);
        string cli = FindNewestFile(Path.Combine(options.ArtifactRoot, "peersharp-cli"), "peersharp-cli.dll")
            ?? throw new FileNotFoundException("PeerSharp CLI build not found. Run the build command first.");
        string buildRoot = Path.Combine(options.ArtifactRoot, "libtorrent-build");
        string client = FindNewestFile(buildRoot, ExecutableName("client_test"))
            ?? throw new FileNotFoundException("libtorrent client_test build not found. Run the build command first.");
        string tester = FindNewestFile(buildRoot, ExecutableName("connection_tester"))
            ?? throw new FileNotFoundException("libtorrent connection_tester build not found. Run the build command first.");
        return new ToolPaths(
            doctor.Dotnet ?? "dotnet",
            doctor.Cmake ?? "cmake",
            cli,
            client,
            tester,
            boostRoot,
            buildRoot);
    }

    public DoctorResult Inspect()
    {
        string? dotnet = FindExecutable("dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        string? cmake = FindCmake();
        string? git = FindExecutable("git", OperatingSystem.IsWindows() ? "git.exe" : "git");
        bool peerSharp = File.Exists(Path.Combine(options.RepositoryRoot, "PeerSharp.slnx"));
        bool libtorrent = Directory.Exists(Path.Combine(options.LibtorrentRoot, ".git"))
            && File.Exists(Path.Combine(options.LibtorrentRoot, "CMakeLists.txt"));
        string? compiler = OperatingSystem.IsWindows() ? FindVisualStudio() : FindExecutable("c++", "c++");
        string? qBittorrent = FindQbittorrent();
        return new DoctorResult(dotnet, cmake, git, compiler, peerSharp, libtorrent, options.LibtorrentRoot, qBittorrent);
    }

    private async Task<string> EnsureBoostAsync(CancellationToken cancellationToken)
    {
        string dependencyRoot = Path.Combine(options.ArtifactRoot, "deps");
        string boostRoot = Path.Combine(dependencyRoot, BoostDirectoryName);
        if (File.Exists(Path.Combine(boostRoot, "boost", "version.hpp"))) return boostRoot;

        Directory.CreateDirectory(dependencyRoot);
        string archive = Path.Combine(dependencyRoot, $"{BoostDirectoryName}.zip");
        if (!File.Exists(archive) || !await HasExpectedHashAsync(archive, cancellationToken).ConfigureAwait(false))
        {
            Console.WriteLine($"Downloading Boost {BoostVersion} ({BoostArchiveUrl})...");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            await using Stream source = await client.GetStreamAsync(BoostArchiveUrl, cancellationToken).ConfigureAwait(false);
            string temporary = archive + ".download";
            await using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            if (!await HasExpectedHashAsync(temporary, cancellationToken).ConfigureAwait(false))
            {
                File.Delete(temporary);
                throw new InvalidDataException("Downloaded Boost archive failed its pinned SHA-256 check.");
            }

            File.Move(temporary, archive, overwrite: true);
        }

        Console.WriteLine($"Extracting Boost {BoostVersion}...");
        if (Directory.Exists(boostRoot)) Directory.Delete(boostRoot, recursive: true);
        ZipFile.ExtractToDirectory(archive, dependencyRoot);
        return boostRoot;
    }

    private async Task<string> BuildPeerSharpAsync(string dotnet, CancellationToken cancellationToken)
    {
        string output = Path.Combine(options.ArtifactRoot, "peersharp-cli");
        Directory.CreateDirectory(output);
        ProcessOutput result = await ProcessUtility.RunAsync(
            dotnet,
            ["publish", Path.Combine(options.RepositoryRoot, "samples", "PeerSharp.Cli", "PeerSharp.Cli.csproj"),
                "-c", "Release", "-o", output, "--nologo"],
            options.RepositoryRoot,
            cancellationToken,
            progress: Console.Out).ConfigureAwait(false);
        EnsureSuccess(result, "PeerSharp CLI build");
        return Path.Combine(output, "peersharp-cli.dll");
    }

    private async Task EnsureLibtorrentBuildSubmoduleAsync(string git, CancellationToken cancellationToken)
    {
        string requiredSource = Path.Combine(options.LibtorrentRoot, "deps", "try_signal", "try_signal.cpp");
        if (File.Exists(requiredSource)) return;

        Console.WriteLine("Initializing libtorrent's pinned deps/try_signal build submodule...");
        ProcessOutput result = await ProcessUtility.RunAsync(
            git,
            ["-C", options.LibtorrentRoot, "submodule", "update", "--init", "--depth", "1", "deps/try_signal"],
            options.LibtorrentRoot,
            cancellationToken,
            progress: Console.Out).ConfigureAwait(false);
        EnsureSuccess(result, "libtorrent submodule initialization");
    }

    private async Task<(string ClientTest, string ConnectionTester, string BuildRoot)> BuildLibtorrentAsync(
        string cmake,
        string boostRoot,
        CancellationToken cancellationToken)
    {
        string buildRoot = Path.Combine(options.ArtifactRoot, "libtorrent-build");
        Directory.CreateDirectory(buildRoot);
        var configure = new List<string>
        {
            "-S", options.LibtorrentRoot,
            "-B", buildRoot,
            "-DBUILD_SHARED_LIBS=OFF",
            "-Dbuild_examples=ON",
            "-Dbuild_tests=OFF",
            "-Dbuild_tools=OFF",
            "-Dpython-bindings=OFF",
            "-Dencryption=OFF",
            "-Dlogging=OFF",
            // The benchmark is native BitTorrent only. Keeping WebTorrent enabled would require
            // libdatachannel's submodules even though no trial exercises that transport.
            "-Dwebtorrent=OFF",
            "-Di2p=OFF",
            $"-DBOOST_ROOT={boostRoot}",
            $"-DBoost_ROOT={boostRoot}"
        };
        if (OperatingSystem.IsWindows())
        {
            configure.AddRange(["-G", VisualStudioGenerator(), "-A", "x64"]);
        }
        else
        {
            configure.Add("-DCMAKE_BUILD_TYPE=Release");
        }

        ProcessOutput configured = await ProcessUtility.RunAsync(
            cmake, configure, options.LibtorrentRoot, cancellationToken, progress: Console.Out).ConfigureAwait(false);
        EnsureSuccess(configured, "libtorrent CMake configure");

        var build = new List<string> { "--build", buildRoot, "--config", "Release", "--parallel", "--target", "client_test", "connection_tester" };
        ProcessOutput built = await ProcessUtility.RunAsync(
            cmake, build, options.LibtorrentRoot, cancellationToken, progress: Console.Out).ConfigureAwait(false);
        EnsureSuccess(built, "libtorrent example build");

        string client = FindNewestFile(buildRoot, ExecutableName("client_test"))
            ?? throw new FileNotFoundException("CMake succeeded but client_test was not found.");
        string tester = FindNewestFile(buildRoot, ExecutableName("connection_tester"))
            ?? throw new FileNotFoundException("CMake succeeded but connection_tester was not found.");
        return (client, tester, buildRoot);
    }

    private static async Task<bool> HasExpectedHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == BoostArchiveSha256;
    }

    private static void EnsureSuccess(ProcessOutput output, string operation)
    {
        if (output.ExitCode != 0)
        {
            throw new InvalidOperationException($"{operation} failed with exit code {output.ExitCode}.\n{output.Combined}");
        }
    }

    private static string? FindCmake()
    {
        string? path = FindExecutable("cmake", OperatingSystem.IsWindows() ? "cmake.exe" : "cmake");
        if (path is not null || !OperatingSystem.IsWindows()) return path;
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] candidates =
        [
            Path.Combine(programFilesX86, "Microsoft Visual Studio", "18", "BuildTools", "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "CMake", "bin", "cmake.exe"),
            Path.Combine(programFilesX86, "Microsoft Visual Studio", "2022", "BuildTools", "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "CMake", "bin", "cmake.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindVisualStudio()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(path)) return null;
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            Arguments = "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo);
        string output = process?.StandardOutput.ReadToEnd().Trim() ?? string.Empty;
        process?.WaitForExit();
        return Directory.Exists(output) ? output : null;
    }

    private static string? FindQbittorrent()
    {
        string executable = OperatingSystem.IsWindows() ? "qbittorrent.exe" : "qbittorrent";
        string? path = FindExecutable("qbittorrent", executable);
        if (path is not null || !OperatingSystem.IsWindows()) return path;
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qBittorrent", executable),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "qBittorrent", executable)
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string VisualStudioGenerator()
    {
        string? installation = FindVisualStudio();
        if (installation?.Contains($"{Path.DirectorySeparatorChar}18{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Visual Studio 18 2026";
        }

        return "Visual Studio 17 2022";
    }

    private static string? FindExecutable(string command, string executableName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory.Trim('"'), executableName))
            .FirstOrDefault(File.Exists);
        return path ?? (File.Exists(command) ? Path.GetFullPath(command) : null);
    }

    private static string? FindNewestFile(string root, string fileName)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

    private static string ExecutableName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
}

internal sealed record DoctorResult(
    string? Dotnet,
    string? Cmake,
    string? Git,
    string? Compiler,
    bool PeerSharpCheckout,
    bool LibtorrentCheckout,
    string LibtorrentRoot,
    string? QBittorrent)
{
    public bool CanBuild => Dotnet is not null && Cmake is not null && Git is not null && Compiler is not null
        && PeerSharpCheckout && LibtorrentCheckout;

    public void Print(TextWriter output)
    {
        output.WriteLine("Benchmark prerequisites:");
        PrintValue(output, ".NET SDK", Dotnet);
        PrintValue(output, "CMake", Cmake);
        PrintValue(output, "Git", Git);
        PrintValue(output, "C++ toolchain", Compiler);
        PrintValue(output, "PeerSharp checkout", PeerSharpCheckout ? "found" : null);
        PrintValue(output, $"libtorrent checkout ({LibtorrentRoot})", LibtorrentCheckout ? "found" : null);
        output.WriteLine($"  {(QBittorrent is null ? "optional" : "ok"),-7} qBittorrent validation target: {QBittorrent ?? "not found"}");
        output.WriteLine(CanBuild ? "Ready to build." : "One or more required items are missing.");
    }

    private static void PrintValue(TextWriter output, string name, string? value)
        => output.WriteLine($"  {(value is null ? "MISSING" : "ok"),-7} {name}: {value ?? "not found"}");
}
