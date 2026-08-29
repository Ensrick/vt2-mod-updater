using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;
using VT2ModUpdater.ViewModels;

namespace VT2ModUpdater.Tests;

public sealed class SourceExactRecoveryRunnerTests
{
    private const string ModId = "modx";
    private const string WorkshopId = "3712896117";
    private const string SourceCommit =
        "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task SuccessfulCoordinatorRequiresMatchingInstalledReadBack()
    {
        using var workshop = new TemporaryDirectory();
        var request = Request(workshop.Path);
        var target = Deployer.GetSyntheticFolder(workshop.Path, WorkshopId);
        var coordinator = new RecordingCoordinator
        {
            Handler = (_, _) => Task.FromResult(Succeeded(target))
        };
        var reader = new RecordingReader
        {
            Result = new SourceExactInstalledReadBack(
                InstalledState(),
                "1.2.3-dev")
        };
        using var runner = new SourceExactRecoveryRunner(coordinator, reader);

        var result = await runner.RecoverAndVerifyAsync(request);

        Assert.Equal(SourceExactRecoveryRunStatus.Succeeded, result.Status);
        Assert.Equal(SourceCommit, result.ReadBack!.State.SourceCommit);
        Assert.Equal("1.2.3-dev", result.ReadBack.InstalledVersion);
        Assert.Same(request, coordinator.Request);
        Assert.Equal(target, reader.TargetPath);
        Assert.Equal("1.2.3-dev", reader.ExpectedVersion);
        Assert.DoesNotContain("latest", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CoordinatorFailureIsTerminalBeforeReadBack()
    {
        using var workshop = new TemporaryDirectory();
        var outcome = new SourceExactRecoveryOutcome(
            SourceExactRecoveryStatus.ArtifactGone,
            SourceExactRecoveryFailure.ResolutionNoSurvivingArchive,
            "historical asset no longer survives");
        var coordinator = new RecordingCoordinator
        {
            Handler = (_, _) => Task.FromResult(outcome)
        };
        var reader = new RecordingReader();
        using var runner = new SourceExactRecoveryRunner(coordinator, reader);

        var result = await runner.RecoverAndVerifyAsync(Request(workshop.Path));

        Assert.Equal(SourceExactRecoveryRunStatus.Failed, result.Status);
        Assert.Same(outcome, result.Outcome);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task MismatchedReadBackCannotBecomeSuccess()
    {
        using var workshop = new TemporaryDirectory();
        var target = Deployer.GetSyntheticFolder(workshop.Path, WorkshopId);
        var coordinator = new RecordingCoordinator
        {
            Handler = (_, _) => Task.FromResult(Succeeded(target))
        };
        var reader = new RecordingReader
        {
            Result = new SourceExactInstalledReadBack(
                InstalledState(sourceCommit:
                    "1123456789abcdef0123456789abcdef01234567"),
                "1.2.3-dev")
        };
        using var runner = new SourceExactRecoveryRunner(coordinator, reader);

        var result = await runner.RecoverAndVerifyAsync(Request(workshop.Path));

        Assert.Equal(SourceExactRecoveryRunStatus.ReadBackFailed, result.Status);
        Assert.Null(result.ReadBack);
        Assert.Contains("differs", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongHistoricalVersionMarkerCannotBecomeSuccess()
    {
        using var workshop = new TemporaryDirectory();
        var target = Deployer.GetSyntheticFolder(workshop.Path, WorkshopId);
        var coordinator = new RecordingCoordinator
        {
            Handler = (_, _) => Task.FromResult(Succeeded(target))
        };
        var reader = new RecordingReader
        {
            Result = new SourceExactInstalledReadBack(
                InstalledState(),
                "different-version")
        };
        using var runner = new SourceExactRecoveryRunner(coordinator, reader);

        var result = await runner.RecoverAndVerifyAsync(Request(workshop.Path));

        Assert.Equal(SourceExactRecoveryRunStatus.ReadBackFailed, result.Status);
        Assert.Null(result.ReadBack);
        Assert.Contains("version marker differs", result.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulCoordinatorMustCarryCanonicalResolvedVersion()
    {
        using var workshop = new TemporaryDirectory();
        var target = Deployer.GetSyntheticFolder(workshop.Path, WorkshopId);
        var coordinator = new RecordingCoordinator
        {
            Handler = (_, _) => Task.FromResult(new SourceExactRecoveryOutcome(
                SourceExactRecoveryStatus.Succeeded,
                SourceExactRecoveryFailure.None,
                "installed",
                target))
        };
        var reader = new RecordingReader();
        using var runner = new SourceExactRecoveryRunner(coordinator, reader);

        var result = await runner.RecoverAndVerifyAsync(Request(workshop.Path));

        Assert.Equal(SourceExactRecoveryRunStatus.ReadBackFailed, result.Status);
        Assert.Equal(0, reader.Calls);
        Assert.Contains("canonical resolved version", result.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellationEscapingCoordinatorIsContained()
    {
        using var workshop = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var coordinator = new RecordingCoordinator
        {
            Handler = (_, token) => Task.FromCanceled<SourceExactRecoveryOutcome>(token)
        };
        var reader = new RecordingReader();
        using var runner = new SourceExactRecoveryRunner(coordinator, reader);

        var result = await runner.RecoverAndVerifyAsync(
            Request(workshop.Path),
            cancellation.Token);

        Assert.Equal(SourceExactRecoveryRunStatus.Cancelled, result.Status);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task UnexpectedCoordinatorExceptionBecomesTerminalFailure()
    {
        using var workshop = new TemporaryDirectory();
        var coordinator = new RecordingCoordinator
        {
            Handler = (_, _) => throw new IOException("unexpected coordinator fault")
        };
        var reader = new RecordingReader();
        using var runner = new SourceExactRecoveryRunner(coordinator, reader);

        var result = await runner.RecoverAndVerifyAsync(Request(workshop.Path));

        Assert.Equal(SourceExactRecoveryRunStatus.Failed, result.Status);
        Assert.Equal(SourceExactRecoveryStatus.ContractFailure, result.Outcome.Status);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public void InstalledReaderReadsOnlyExactSyntheticSidecars()
    {
        using var workshop = new TemporaryDirectory();
        var request = Request(workshop.Path);
        var target = Deployer.GetSyntheticFolder(workshop.Path, WorkshopId);
        Directory.CreateDirectory(target);
        File.WriteAllBytes(
            Path.Combine(target, SourceExactInstalledState.Filename),
            SourceExactInstalledState.Serialize(InstalledState()));
        File.WriteAllBytes(
            Path.Combine(target, SourceExactZipStager.VersionMarkerFilename),
            Encoding.ASCII.GetBytes("1.2.3-dev"));
        var reader = new SourceExactInstalledStateReader();

        var result = reader.Read(request, target, "1.2.3-dev");

        Assert.Equal(SourceCommit, result.State.SourceCommit);
        Assert.Equal("1.2.3-dev", result.InstalledVersion);
        Assert.Throws<InvalidDataException>(() => reader.Read(
            request,
            Path.Combine(workshop.Path, WorkshopId),
            "1.2.3-dev"));
    }

    [Fact]
    public void InstalledReaderRejectsMalformedOrMissingReadBack()
    {
        using var workshop = new TemporaryDirectory();
        var request = Request(workshop.Path);
        var target = Deployer.GetSyntheticFolder(workshop.Path, WorkshopId);
        Directory.CreateDirectory(target);
        File.WriteAllText(
            Path.Combine(target, SourceExactInstalledState.Filename),
            "not-json");
        File.WriteAllText(
            Path.Combine(target, SourceExactZipStager.VersionMarkerFilename),
            "1.2.3-dev");
        var reader = new SourceExactInstalledStateReader();

        Assert.Throws<InvalidDataException>(() =>
            reader.Read(request, target, "1.2.3-dev"));
        File.Delete(Path.Combine(target, SourceExactInstalledState.Filename));
        Assert.Throws<InvalidDataException>(() =>
            reader.Read(request, target, "1.2.3-dev"));
    }

    [Theory]
    [InlineData(" 1.2.3-dev")]
    [InlineData("1.2.3-dev ")]
    [InlineData("1.2.3\ndev")]
    [InlineData("1.2.3\0dev")]
    public void InstalledReaderRejectsNonCanonicalVersionText(string version)
    {
        using var workshop = new TemporaryDirectory();
        var request = Request(workshop.Path);
        var target = Deployer.GetSyntheticFolder(workshop.Path, WorkshopId);
        Directory.CreateDirectory(target);
        File.WriteAllBytes(
            Path.Combine(target, SourceExactInstalledState.Filename),
            SourceExactInstalledState.Serialize(InstalledState()));
        File.WriteAllBytes(
            Path.Combine(target, SourceExactZipStager.VersionMarkerFilename),
            Encoding.ASCII.GetBytes(version));
        var reader = new SourceExactInstalledStateReader();

        Assert.Throws<InvalidDataException>(() =>
            reader.Read(request, target, "1.2.3-dev"));
    }

    [Fact]
    public void InstalledReaderRequiresByteExactResolvedVersionMarker()
    {
        using var workshop = new TemporaryDirectory();
        var request = Request(workshop.Path);
        var target = Deployer.GetSyntheticFolder(workshop.Path, WorkshopId);
        Directory.CreateDirectory(target);
        File.WriteAllBytes(
            Path.Combine(target, SourceExactInstalledState.Filename),
            SourceExactInstalledState.Serialize(InstalledState()));
        File.WriteAllBytes(
            Path.Combine(target, SourceExactZipStager.VersionMarkerFilename),
            Encoding.ASCII.GetBytes("1.2.3-dev"));
        var reader = new SourceExactInstalledStateReader();

        var error = Assert.Throws<InvalidDataException>(() =>
            reader.Read(request, target, "1.2.4-dev"));
        Assert.Contains("byte-for-byte", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecoveryRunnerHasNoLatestOrLegacyDeployDependency()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "VT2ModUpdater",
            "Services",
            "SourceExactRecoveryRunner.cs"));

        Assert.DoesNotContain("GitHubReleaseClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DeployZipBytes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLatestRelease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/latest", source, StringComparison.Ordinal);
    }

    internal static SourceExactInstalledStateDocument InstalledState(
        string modId = ModId,
        string workshopId = WorkshopId,
        string sourceCommit = SourceCommit)
    {
        const string hash =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var outputs = Array.AsReadOnly(new[]
        {
            new SourceExactInstalledOutput("modx.mod", 17, hash)
        });
        var fingerprint = RecoveryRecordContract.ComputeOutputFingerprint(new[]
        {
            new RecoveryOutputFile("modx.mod", 17, hash, "")
        });
        return new SourceExactInstalledStateDocument(
            SourceExactInstalledState.SchemaVersion,
            SourceExactInstalledState.Authority,
            modId,
            workshopId,
            sourceCommit,
            "mods-origin-2026-08-28",
            100,
            200,
            "modx.zip",
            546,
            hash,
            fingerprint,
            outputs);
    }

    internal static SourceExactRecoveryRequest Request(string workshopRoot) => new(
        RecoveryRecordContract.Repository,
        ModId,
        WorkshopId,
        SourceCommit,
        workshopRoot);

    internal static SourceExactRecoveryOutcome Succeeded(
        string target,
        string version = "1.2.3-dev") => new(
        SourceExactRecoveryStatus.Succeeded,
        SourceExactRecoveryFailure.None,
        "installed",
        target,
        ResolvedVersion: version);

    internal static string RepositoryRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null &&
               !File.Exists(Path.Combine(cursor.FullName, "vt2-mod-updater.sln")))
        {
            cursor = cursor.Parent;
        }
        return cursor?.FullName ??
            throw new DirectoryNotFoundException(
                "cannot locate updater repository root");
    }

    private sealed class RecordingCoordinator : ISourceExactRecoveryCoordinator
    {
        internal Func<SourceExactRecoveryRequest?, CancellationToken,
            Task<SourceExactRecoveryOutcome>> Handler
        { get; init; } =
                (_, _) => throw new InvalidOperationException("no outcome configured");
        internal SourceExactRecoveryRequest? Request { get; private set; }
        internal int DisposeCalls { get; private set; }

        public Task<SourceExactRecoveryOutcome> RecoverAsync(
            SourceExactRecoveryRequest? request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Handler(request, cancellationToken);
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class RecordingReader : ISourceExactInstalledStateReader
    {
        internal SourceExactInstalledReadBack? Result { get; init; }
        internal int Calls { get; private set; }
        internal string? TargetPath { get; private set; }
        internal string? ExpectedVersion { get; private set; }

        public SourceExactInstalledReadBack Read(
            SourceExactRecoveryRequest request,
            string targetPath,
            string expectedVersion)
        {
            Calls++;
            TargetPath = targetPath;
            ExpectedVersion = expectedVersion;
            return Result ?? throw new InvalidDataException("read-back unavailable");
        }
    }
}

public sealed class MainViewModelSourceExactRecoveryTests
{
    private const string SourceCommit =
        "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void SelectionAndCommitPopulationDoNotStartRecovery()
    {
        using var workshop = new TemporaryDirectory();
        var runner = new RecordingRunner();
        using var viewModel = new MainViewModel(runner, workshop.Path);
        var row = Row();

        viewModel.Mods.Add(row);
        viewModel.SelectedMod = row;

        Assert.Equal(SourceCommit, viewModel.SourceExactCommitInput);
        Assert.Equal(0, runner.Calls);
        Assert.True(viewModel.RecoverExactSourceCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExplicitActionComposesExactRequestAndUpdatesOnlyAfterReadBack()
    {
        using var workshop = new TemporaryDirectory();
        var runner = new RecordingRunner
        {
            Handler = (request, _) => Task.FromResult(Success(request))
        };
        using var viewModel = new MainViewModel(
            runner,
            workshop.Path,
            startRefresh: false);
        var row = Row();
        viewModel.Mods.Add(row);
        viewModel.SelectedMod = row;

        await viewModel.RecoverExactSourceAsync();

        Assert.Equal(1, runner.Calls);
        Assert.Equal(RecoveryRecordContract.Repository, runner.Request!.Repository);
        Assert.Equal("modx", runner.Request.ModId);
        Assert.Equal("3712896117", runner.Request.WorkshopId);
        Assert.Equal(SourceCommit, runner.Request.SourceCommit);
        Assert.Equal(workshop.Path, runner.Request.WorkshopContentRoot);
        Assert.Equal("1.2.3-dev", row.InstalledVersion);
        Assert.Equal(SourceCommit, row.InstalledSourceCommit);
        Assert.Contains("read-back matched", viewModel.SourceExactRecoveryMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedRecoveryLeavesExistingRowStateUntouched()
    {
        using var workshop = new TemporaryDirectory();
        var runner = new RecordingRunner
        {
            Handler = (_, _) => Task.FromResult(Failed())
        };
        using var viewModel = new MainViewModel(runner, workshop.Path);
        var row = Row();
        row.InstalledVersion = "prior-version";
        viewModel.Mods.Add(row);
        viewModel.SelectedMod = row;

        await viewModel.RecoverExactSourceAsync();

        Assert.Equal("prior-version", row.InstalledVersion);
        Assert.Null(row.InstalledSourceCommit);
        Assert.Contains("failed", viewModel.SourceExactRecoveryMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData(" 0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789abcdef0123456789abcdef01234567 ")]
    public async Task MalformedCommitIsRejectedBeforeRunner(string commit)
    {
        using var workshop = new TemporaryDirectory();
        var runner = new RecordingRunner();
        using var viewModel = new MainViewModel(runner, workshop.Path);
        var row = Row();
        viewModel.Mods.Add(row);
        viewModel.SelectedMod = row;
        viewModel.SourceExactCommitInput = commit;

        Assert.False(viewModel.RecoverExactSourceCommand.CanExecute(null));
        await viewModel.RecoverExactSourceAsync();

        Assert.Equal(0, runner.Calls);
        Assert.Contains("40-character lowercase", viewModel.SourceExactRecoveryMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelCommandCancelsOneActiveActionAndRestoresCommandState()
    {
        using var workshop = new TemporaryDirectory();
        var runner = new RecordingRunner
        {
            Handler = async (_, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("delay unexpectedly completed");
                }
                catch (OperationCanceledException)
                {
                    return Cancelled();
                }
            }
        };
        using var viewModel = new MainViewModel(runner, workshop.Path);
        var row = Row();
        viewModel.Mods.Add(row);
        viewModel.SelectedMod = row;

        var active = viewModel.RecoverExactSourceAsync();
        Assert.True(viewModel.IsSourceExactRecoveryBusy);
        Assert.False(viewModel.RecoverExactSourceCommand.CanExecute(null));
        Assert.True(viewModel.CancelExactSourceRecoveryCommand.CanExecute(null));
        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        Assert.False(viewModel.UpdateOneCommand.CanExecute(row));
        Assert.False(viewModel.UpdateAllCommand.CanExecute(null));

        var other = Row();
        viewModel.SelectedMod = other;
        Assert.Same(row, viewModel.SelectedMod);

        viewModel.CancelExactSourceRecovery();
        await active;

        Assert.Equal(1, runner.Calls);
        Assert.False(viewModel.IsSourceExactRecoveryBusy);
        Assert.True(viewModel.RecoverExactSourceCommand.CanExecute(null));
        Assert.Contains("cancelled", viewModel.SourceExactRecoveryMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaterInstalledVersionAssignmentClearsSessionExactBadge()
    {
        var row = Row();
        row.InstalledSourceCommit = SourceCommit;

        row.InstalledVersion = "ordinary-update-version";

        Assert.Null(row.InstalledSourceCommit);
        Assert.DoesNotContain("SOURCE_EXACT", row.StateLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReentrantActionDoesNotStartSecondRunner()
    {
        using var workshop = new TemporaryDirectory();
        var completion = new TaskCompletionSource<SourceExactRecoveryRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingRunner
        {
            Handler = (_, _) => completion.Task
        };
        using var viewModel = new MainViewModel(runner, workshop.Path);
        var row = Row();
        viewModel.Mods.Add(row);
        viewModel.SelectedMod = row;

        var first = viewModel.RecoverExactSourceAsync();
        await viewModel.RecoverExactSourceAsync();
        Assert.Equal(1, runner.Calls);
        Assert.Contains("already running", viewModel.SourceExactRecoveryMessage,
            StringComparison.OrdinalIgnoreCase);

        completion.SetResult(Success(runner.Request!));
        await first;
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task DisposeCancelsThenDefersRunnerDisposalUntilActionCompletes()
    {
        using var workshop = new TemporaryDirectory();
        var runner = new RecordingRunner
        {
            Handler = async (_, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("delay unexpectedly completed");
                }
                catch (OperationCanceledException)
                {
                    return Cancelled();
                }
            }
        };
        var viewModel = new MainViewModel(runner, workshop.Path);
        var row = Row();
        viewModel.Mods.Add(row);
        viewModel.SelectedMod = row;

        var active = viewModel.RecoverExactSourceAsync();
        viewModel.Dispose();
        Assert.Equal(0, runner.DisposeCalls);

        await active;
        Assert.Equal(1, runner.DisposeCalls);
    }

    [Fact]
    public async Task InFlightRefreshRefusesRecoveryUntilManifestReplacementCompletes()
    {
        using var workshop = new TemporaryDirectory();
        var refreshEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCompletion = new TaskCompletionSource<GitHubRelease>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClient = new RecordingReleaseClient
        {
            LatestHandler = _ =>
            {
                refreshEntered.TrySetResult(true);
                return refreshCompletion.Task;
            },
            Manifest = Manifest(Row().Entry)
        };
        var runner = new RecordingRunner();
        using var viewModel = new MainViewModel(
            runner,
            workshop.Path,
            releaseClient: releaseClient,
            workshopPathResolver: () => workshop.Path);
        var originalRow = Row();
        viewModel.Mods.Add(originalRow);
        viewModel.SelectedMod = originalRow;

        var refresh = viewModel.RefreshWithAdmissionAsync();
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(viewModel.RecoverExactSourceCommand.CanExecute(null));

        await viewModel.RecoverExactSourceAsync();
        Assert.Equal(0, runner.Calls);
        Assert.Contains("operation is already running",
            viewModel.SourceExactRecoveryMessage,
            StringComparison.OrdinalIgnoreCase);

        refreshCompletion.SetResult(new GitHubRelease());
        await refresh;

        Assert.Single(viewModel.Mods);
        Assert.NotSame(originalRow, viewModel.SelectedMod);
        Assert.True(viewModel.RecoverExactSourceCommand.CanExecute(null));
    }

    [Fact]
    public async Task InFlightOrdinaryUpdateCannotRaceExactRecoveryDeployment()
    {
        using var workshop = new TemporaryDirectory();
        var zip = MakeZip(("modx.mod", "ordinary release"));
        var assetEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var assetCompletion = new TaskCompletionSource<byte[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = Row().Entry;
        entry.Sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var releaseClient = new RecordingReleaseClient
        {
            Manifest = Manifest(entry),
            AssetHandler = (_, _, _) =>
            {
                assetEntered.TrySetResult(true);
                return assetCompletion.Task;
            }
        };
        var runner = new RecordingRunner();
        using var viewModel = new MainViewModel(
            runner,
            workshop.Path,
            releaseClient: releaseClient,
            workshopPathResolver: () => workshop.Path);
        await viewModel.RefreshWithAdmissionAsync();
        var row = Assert.Single(viewModel.Mods);

        var update = viewModel.UpdateOneWithAdmissionAsync(row);
        await assetEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(viewModel.RecoverExactSourceCommand.CanExecute(null));

        await viewModel.RecoverExactSourceAsync();
        Assert.Equal(0, runner.Calls);
        Assert.Contains("operation is already running",
            viewModel.SourceExactRecoveryMessage,
            StringComparison.OrdinalIgnoreCase);

        assetCompletion.SetResult(zip);
        await update;

        Assert.Equal("latest-version", row.InstalledVersion);
        Assert.True(viewModel.RecoverExactSourceCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecoverySuccessDoesNotUpdateRowRemovedFromManifestGeneration()
    {
        using var workshop = new TemporaryDirectory();
        var completion = new TaskCompletionSource<SourceExactRecoveryRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingRunner
        {
            Handler = (_, _) => completion.Task
        };
        using var viewModel = new MainViewModel(runner, workshop.Path);
        var row = Row();
        viewModel.Mods.Add(row);
        viewModel.SelectedMod = row;

        var recovery = viewModel.RecoverExactSourceAsync();
        viewModel.Mods.Clear();
        completion.SetResult(Success(runner.Request!));
        await recovery;

        Assert.Equal("—", row.InstalledVersion);
        Assert.Null(row.InstalledSourceCommit);
        Assert.Contains("manifest selection changed",
            viewModel.SourceExactRecoveryMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void XamlExposesRecoveryAsSeparateAdvancedAction()
    {
        var path = Path.Combine(
            SourceExactRecoveryRunnerTests.RepositoryRoot(),
            "src",
            "VT2ModUpdater",
            "MainWindow.xaml");
        var document = XDocument.Load(path);
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var buttons = document.Descendants(presentation + "Button").ToArray();

        var recover = Assert.Single(buttons, button =>
            (string?)button.Attribute("Content") == "Recover Exact Source");
        Assert.Equal(
            "{Binding RecoverExactSourceCommand}",
            (string?)recover.Attribute("Command"));
        var cancel = Assert.Single(buttons, button =>
            (string?)button.Attribute("Content") == "Cancel Recovery");
        Assert.Equal(
            "{Binding CancelExactSourceRecoveryCommand}",
            (string?)cancel.Attribute("Command"));
        Assert.Single(document.Descendants(presentation + "TextBox"), textBox =>
            ((string?)textBox.Attribute("Text"))?.Contains(
                "SourceExactCommitInput",
                StringComparison.Ordinal) == true);

        var update = Assert.Single(buttons, button =>
            (string?)button.Attribute("Content") == "Update");
        Assert.Equal(
            "{Binding DataContext.UpdateOneCommand, RelativeSource={RelativeSource AncestorType=Window}}",
            (string?)update.Attribute("Command"));
    }

    [Fact]
    public void OrdinaryUpdateMethodDoesNotCallRecoveryComposition()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceExactRecoveryRunnerTests.RepositoryRoot(),
            "src",
            "VT2ModUpdater",
            "ViewModels",
            "MainViewModel.cs"));
        var start = source.IndexOf(
            "private async Task UpdateOneAsync",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private async Task<byte[]?> DownloadAndVerifyAsync",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var updateMethod = source[start..end];

        Assert.Contains("Deployer.DeployZipBytes", updateMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SourceExact", updateMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Recover", updateMethod,
            StringComparison.Ordinal);
    }

    private static ModRow Row() => new(new ManifestEntry
    {
        ModId = "modx",
        FriendlyName = "Mod X",
        WorkshopId = "3712896117",
        Version = "latest-version",
        AssetFilename = "modx.zip",
        SourceCommit = SourceCommit
    });

    private static ReleaseManifest Manifest(ManifestEntry entry) => new()
    {
        ReleaseTag = "test-release",
        Mods = new List<ManifestEntry> { entry }
    };

    private static byte[] MakeZip(params (string Name, string Body)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            foreach (var (name, body) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var target = entry.Open();
                using var writer = new StreamWriter(target);
                writer.Write(body);
            }
        }
        return stream.ToArray();
    }

    private static SourceExactRecoveryRunResult Success(
        SourceExactRecoveryRequest request)
    {
        var state = SourceExactRecoveryRunnerTests.InstalledState(
            request.ModId,
            request.WorkshopId,
            request.SourceCommit);
        var target = Deployer.GetSyntheticFolder(
            request.WorkshopContentRoot,
            request.WorkshopId);
        return new SourceExactRecoveryRunResult(
            SourceExactRecoveryRunStatus.Succeeded,
            SourceExactRecoveryRunnerTests.Succeeded(target),
            "Recovered exact source; installed-state read-back matched.",
            new SourceExactInstalledReadBack(state, "1.2.3-dev"));
    }

    private static SourceExactRecoveryRunResult Failed()
    {
        var outcome = new SourceExactRecoveryOutcome(
            SourceExactRecoveryStatus.ArtifactGone,
            SourceExactRecoveryFailure.ResolutionNoSurvivingArchive,
            "gone");
        return new SourceExactRecoveryRunResult(
            SourceExactRecoveryRunStatus.Failed,
            outcome,
            "Source-exact recovery failed: gone");
    }

    private static SourceExactRecoveryRunResult Cancelled()
    {
        var outcome = new SourceExactRecoveryOutcome(
            SourceExactRecoveryStatus.Cancelled,
            SourceExactRecoveryFailure.Cancelled,
            "cancelled");
        return new SourceExactRecoveryRunResult(
            SourceExactRecoveryRunStatus.Cancelled,
            outcome,
            "Source-exact recovery was cancelled; no new install was authorized.");
    }

    private sealed class RecordingRunner : ISourceExactRecoveryRunner
    {
        internal Func<SourceExactRecoveryRequest, CancellationToken,
            Task<SourceExactRecoveryRunResult>> Handler
        { get; init; } =
                (_, _) => throw new InvalidOperationException("no result configured");
        internal int Calls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal SourceExactRecoveryRequest? Request { get; private set; }

        public Task<SourceExactRecoveryRunResult> RecoverAndVerifyAsync(
            SourceExactRecoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            return Handler(request, cancellationToken);
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class RecordingReleaseClient : IReleaseClient
    {
        internal Func<CancellationToken, Task<GitHubRelease>> LatestHandler
        { get; init; } = _ => Task.FromResult(new GitHubRelease());

        internal Func<GitHubRelease, CancellationToken, Task<ReleaseManifest>>
            ManifestHandler
        { get; init; } = (_, _) => Task.FromResult(new ReleaseManifest());

        internal Func<GitHubRelease, string, CancellationToken, Task<byte[]>>
            AssetHandler
        { get; init; } = (_, _, _) =>
            throw new InvalidOperationException("no asset configured");

        internal ReleaseManifest? Manifest { get; init; }

        public Task<GitHubRelease> GetLatestReleaseAsync(
            CancellationToken cancellationToken = default) =>
            LatestHandler(cancellationToken);

        public Task<ReleaseManifest> DownloadManifestAsync(
            GitHubRelease release,
            CancellationToken cancellationToken = default) =>
            Manifest is not null
                ? Task.FromResult(Manifest)
                : ManifestHandler(release, cancellationToken);

        public Task<byte[]> DownloadAssetAsync(
            GitHubRelease release,
            string assetFilename,
            CancellationToken cancellationToken = default) =>
            AssetHandler(release, assetFilename, cancellationToken);

        public void Dispose() { }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vt2-updater-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
