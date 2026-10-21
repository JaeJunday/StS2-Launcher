using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SteamKit2;
using SteamKit2.CDN;

namespace STS2Mobile.Steam;

public class DownloadProgress
{
    public long TotalBytes;
    public long DownloadedBytes;
    public int TotalFiles;
    public int CompletedFiles;
    public string CurrentFile;

    public double Percentage => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes * 100.0 : 0;
}

// Downloads game files from Steam CDN using SteamKit2. Supports delta updates
// by comparing manifests, concurrent chunk downloads, and server rotation with
// retry logic. Also patches the PCK to remove the Sentry plugin (no ARM64 build).
public class DepotDownloader : IDisposable
{
    private const uint AppId = 2868840;
    private const int MaxRetries = 5;

    // Files fetched in parallel, and chunks of one file fetched in parallel. The
    // game's PCK is a single multi-hundred-MB file, so per-file chunk pipelining is
    // what actually determines throughput; chunk requests are tiny range GETs.
    private const int MaxConcurrentDownloads = 4;
    private const int ChunkConcurrency = 6;
    private const int ChunkTimeoutSeconds = 40;
    private const long ProgressLogIntervalBytes = 64L * 1024 * 1024;

    // Steam branches. The beta ("public-beta", open, no password) tracks the
    // public beta build; "public" is the default release build.
    public const string PublicBranch = "public";
    public const string BetaBranch = "public-beta";

    // Branch to download / check updates for. This fork defaults to public-beta,
    // which is where the current game builds ship; "public" stays selectable.
    public string Branch { get; set; } = BetaBranch;

    private readonly SteamConnection _connection;
    private readonly string _gameDir;
    private readonly string _stateDir;
    private readonly Client _cdnClient;
    private readonly DownloadProgress _progress = new();

    private IReadOnlyList<Server> _servers;
    private int _serverIndex;
    private long _lastProgressLog;
    private DateTime _downloadStartUtc = DateTime.UtcNow;
    private readonly Dictionary<(uint, string), string> _cdnAuthTokens = new();
    private readonly Dictionary<
        (uint DepotId, ulong ManifestId),
        (ulong Code, DateTime Expiry)
    > _manifestRequestCodes = new();    private readonly Dictionary<
        uint,
        SteamApps.PICSProductInfoCallback.PICSProductInfo
    > _appInfoCache = new();

    public event Action<DownloadProgress> ProgressChanged;
    public event Action<string> LogMessage;

    public DepotDownloader(SteamConnection connection, string dataDir)
    {
        _connection = connection;
        _gameDir = Path.Combine(dataDir, "game");
        _stateDir = Path.Combine(dataDir, "download_state");
        _cdnClient = new Client(connection.Client);
    }

    // Returns true if any depot has a newer manifest than what's cached locally.
    public async Task<bool> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        _connection.SuspendIdleTimeout();
        try
        {
            Directory.CreateDirectory(_stateDir);

            ulong accessToken = _connection.AppAccessToken;
            var infoResult = await _connection.Apps.PICSGetProductInfo(
                new[] { new SteamApps.PICSRequest(AppId, accessToken) },
                Enumerable.Empty<SteamApps.PICSRequest>()
            );

            SteamApps.PICSProductInfoCallback.PICSProductInfo appInfo = null;
            foreach (var cb in infoResult.Results)
            {
                if (cb.Apps.TryGetValue(AppId, out var info))
                {
                    appInfo = info;
                    break;
                }
            }

            if (appInfo == null)
                throw new Exception("Failed to get app info from Steam");

            _appInfoCache[AppId] = appInfo;
            var depots = await ParseDepotsAsync(appInfo.KeyValues["depots"]);

            foreach (var (depotId, manifestId) in depots)
            {
                ct.ThrowIfCancellationRequested();
                if (LoadCachedManifestId(depotId) != manifestId)
                {
                    Log($"Update available: depot {depotId} manifest changed");
                    return true;
                }
            }

            Log("Game is up to date");
            return false;
        }
        finally
        {
            _connection.ResumeIdleTimeout();
        }
    }

    public async Task DownloadAsync(CancellationToken ct = default)
    {
        _connection.SuspendIdleTimeout();
        try
        {
            Directory.CreateDirectory(_gameDir);
            Directory.CreateDirectory(_stateDir);

            Log("Fetching app info...");

            ulong accessToken = _connection.AppAccessToken;
            var infoResult = await _connection.Apps.PICSGetProductInfo(
                new[] { new SteamApps.PICSRequest(AppId, accessToken) },
                Enumerable.Empty<SteamApps.PICSRequest>()
            );

            SteamApps.PICSProductInfoCallback.PICSProductInfo appInfo = null;
            foreach (var cb in infoResult.Results)
            {
                if (cb.Apps.TryGetValue(AppId, out var info))
                {
                    appInfo = info;
                    break;
                }
            }

            if (appInfo == null)
                throw new Exception("Failed to get app info from Steam");

            _appInfoCache[AppId] = appInfo;
            var depotSection = appInfo.KeyValues["depots"];
            var depots = await ParseDepotsAsync(depotSection);
            if (depots.Count == 0)
                throw new Exception("No downloadable depots found");

            Log("Getting CDN servers...");
            var allServers = await ContentServerDirectoryService.LoadAsync(
                _connection.Configuration,
                ct
            );
            if (allServers == null || allServers.Count == 0)
                throw new Exception("No CDN servers available");

            _servers = allServers
                .Where(s => s.Type == "SteamCache" || s.Type == "CDN")
                .OrderBy(s => s.WeightedLoad)
                .ToList();

            if (_servers.Count == 0)
                _servers = allServers.ToList();

            Log($"Using {_servers.Count} CDN servers");

            foreach (var (depotId, manifestId) in depots)
            {
                ct.ThrowIfCancellationRequested();
                await DownloadDepotAsync(depotId, manifestId, ct);
            }

            Log("All game files downloaded!");

            // Remove Sentry plugin references (no android.arm64 build exists).
            PatchGamePck(Path.Combine(_gameDir, "SlayTheSpire2.pck"));
        }
        finally
        {
            _connection.ResumeIdleTimeout();
        }
    }

    private async Task<List<(uint DepotId, ulong ManifestId)>> ParseDepotsAsync(
        KeyValue depotSection
    )
    {
        var result = new List<(uint, ulong)>();

        foreach (var depot in depotSection.Children)
        {
            if (!uint.TryParse(depot.Name, out var depotId))
                continue;

            // Skip non-Windows depots.
            var config = depot["config"];
            if (config != KeyValue.Invalid)
            {
                var oslist = config["oslist"]?.Value;
                if (oslist != null && oslist.Length > 0 && !oslist.Contains("windows"))
                {
                    Log($"Skipping depot {depotId} (OS: {oslist})");
                    continue;
                }
            }

            var manifests = depot["manifests"];

            // Manifest may be defined under a different app via depotfromapp.
            if (manifests == KeyValue.Invalid)
            {
                var depotFromApp = depot["depotfromapp"];
                if (
                    depotFromApp != KeyValue.Invalid
                    && depotFromApp.Value != null
                    && uint.TryParse(depotFromApp.Value, out var otherAppId)
                )
                {
                    Log($"Depot {depotId} references app {otherAppId}, fetching...");
                    var otherAppInfo = await GetAppInfoAsync(otherAppId);
                    if (otherAppInfo != null)
                    {
                        var otherDepots = otherAppInfo.KeyValues["depots"];
                        var otherDepot = otherDepots[depotId.ToString()];
                        if (otherDepot != KeyValue.Invalid)
                            manifests = otherDepot["manifests"];
                    }
                }

                if (manifests == KeyValue.Invalid)
                    continue;
            }

            var gidNode = manifests[Branch]["gid"];
            if (gidNode == KeyValue.Invalid || gidNode.Value == null)
            {
                if (Branch != PublicBranch)
                {
                    Log(
                        $"Depot {depotId} has no '{Branch}' manifest, falling back to '{PublicBranch}'"
                    );
                    gidNode = manifests[PublicBranch]["gid"];
                }
                if (gidNode == KeyValue.Invalid || gidNode.Value == null)
                    continue;
            }

            if (!ulong.TryParse(gidNode.Value, out var manifestId))
                continue;

            Log($"Found depot {depotId} manifest {manifestId}");
            result.Add((depotId, manifestId));
        }

        return result;
    }

    private async Task<SteamApps.PICSProductInfoCallback.PICSProductInfo> GetAppInfoAsync(
        uint appId
    )
    {
        if (_appInfoCache.TryGetValue(appId, out var cached))
            return cached;

        var tokenResult = await _connection.Apps.PICSGetAccessTokens(
            new[] { appId },
            Enumerable.Empty<uint>()
        );
        ulong token = 0;
        tokenResult.AppTokens?.TryGetValue(appId, out token);

        var infoResult = await _connection.Apps.PICSGetProductInfo(
            new[] { new SteamApps.PICSRequest(appId, token) },
            Enumerable.Empty<SteamApps.PICSRequest>()
        );

        foreach (var cb in infoResult.Results)
        {
            if (cb.Apps.TryGetValue(appId, out var info))
            {
                _appInfoCache[appId] = info;
                return info;
            }
        }

        return null;
    }

    private Server GetNextServer()
    {
        var idx = Interlocked.Increment(ref _serverIndex);
        return _servers[((idx % _servers.Count) + _servers.Count) % _servers.Count];
    }

    private async Task<string> GetCdnAuthToken(uint depotId, Server server)
    {
        var key = (depotId, server.Host);
        if (_cdnAuthTokens.TryGetValue(key, out var cached))
            return cached;

        var result = await _connection.Content.GetCDNAuthToken(AppId, depotId, server.Host);
        if (result.Result == EResult.OK)
        {
            _cdnAuthTokens[key] = result.Token;
            return result.Token;
        }

        return null;
    }

    private async Task<ulong> GetManifestRequestCodeAsync(uint depotId, ulong manifestId)
    {
        var codeKey = (depotId, manifestId);
        if (
            _manifestRequestCodes.TryGetValue(codeKey, out var cached)
            && DateTime.UtcNow < cached.Expiry
        )
        {
            return cached.Code;
        }

        var code = await _connection.Content.GetManifestRequestCode(
            depotId,
            AppId,
            manifestId,
            Branch
        );
        if (code == 0)
            throw new Exception(
                $"Failed to get manifest request code for depot {depotId}. "
                    + "Ensure the account owns this app."
            );

        _manifestRequestCodes[codeKey] = (code, DateTime.UtcNow.AddMinutes(5));
        return code;
    }

    private async Task DownloadDepotAsync(uint depotId, ulong manifestId, CancellationToken ct)
    {
        Log($"Processing depot {depotId}...");

        bool isUpdate = LoadCachedManifestId(depotId) != manifestId;

        var keyResult = await _connection.Apps.GetDepotDecryptionKey(depotId, AppId);
        if (keyResult.Result != EResult.OK)
            throw new Exception($"Failed to get depot key for {depotId}: {keyResult.Result}");
        var depotKey = keyResult.DepotKey;

        var manifestRequestCode = await GetManifestRequestCodeAsync(depotId, manifestId);

        Log($"Downloading manifest for depot {depotId}...");
        DepotManifest manifest = null;
        for (int attempt = 0; attempt < MaxRetries && manifest == null; attempt++)
        {
            var server = GetNextServer();
            try
            {
                manifest = await _cdnClient.DownloadManifestAsync(
                    depotId,
                    manifestId,
                    manifestRequestCode,
                    server,
                    depotKey
                );
            }
            catch (SteamKitWebRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                var token = await GetCdnAuthToken(depotId, server);
                if (token != null)
                {
                    manifest = await _cdnClient.DownloadManifestAsync(
                        depotId,
                        manifestId,
                        manifestRequestCode,
                        server,
                        depotKey,
                        cdnAuthToken: token
                    );
                }
            }
            catch (Exception ex) when (attempt < MaxRetries - 1)
            {
                Log($"Manifest download failed (attempt {attempt + 1}): {ex.Message}");
            }
        }

        if (manifest == null)
            throw new Exception(
                $"Failed to download manifest for depot {depotId} after {MaxRetries} attempts"
            );

        var oldManifest = LoadCachedManifest(depotId);

        // Determine which files need downloading: new/changed files from the
        // manifest diff, plus any existing files that fail on-disk SHA-1 verification.
        var filesToDownload = GetFilesNeedingDownload(oldManifest, manifest, isUpdate);
        var filesToDelete = GetFilesToDelete(oldManifest, manifest);

        foreach (var fileName in filesToDelete)
        {
            var path = Path.Combine(_gameDir, fileName.Replace('\\', '/'));
            if (File.Exists(path))
            {
                File.Delete(path);
                Log($"Deleted: {fileName}");
            }
        }

        _progress.TotalFiles = filesToDownload.Count;
        _progress.CompletedFiles = 0;
        _progress.TotalBytes = filesToDownload.Sum(f => (long)f.TotalSize);
        _progress.DownloadedBytes = 0;
        _downloadStartUtc = DateTime.UtcNow;
        _lastProgressLog = 0;
        ReportProgress();

        if (filesToDownload.Count == 0)
        {
            Log($"Depot {depotId}: already up to date");
        }
        else
        {
            Log(
                $"Downloading {filesToDownload.Count} files ({FormatSize(_progress.TotalBytes)}) with {MaxConcurrentDownloads} threads..."
            );

            using var semaphore = new SemaphoreSlim(MaxConcurrentDownloads);
            var tasks = new List<Task>();

            foreach (var file in filesToDownload)
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct);

                tasks.Add(
                    Task.Run(
                        async () =>
                        {
                            try
                            {
                                await DownloadFileAsync(file, depotId, depotKey, ct);
                                Interlocked.Increment(ref _progress.CompletedFiles);
                                ReportProgress();
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        },
                        ct
                    )
                );
            }

            await Task.WhenAll(tasks);
        }

        SaveManifest(depotId, manifest, manifestId);
        Log($"Depot {depotId} complete");
    }

    // Downloads one file, resuming from a previous attempt when possible.
    //
    // Steam's CDN serves ~1 MB range chunks, so a single-file sequential loop is
    // dominated by round-trip latency: the game PCK alone is ~1.4 GB. Chunks are
    // therefore fetched concurrently per file and written at their manifest offsets
    // with RandomAccess (positional writes are safe on a shared handle). The
    // ".downloading" temp file survives restarts; chunks that already hash
    // correctly on disk are skipped, so an interrupted download resumes instead of
    // re-burning hundreds of MB of mobile data.
    private async Task DownloadFileAsync(
        DepotManifest.FileData file,
        uint depotId,
        byte[] depotKey,
        CancellationToken ct
    )
    {
        var fileName = file.FileName.Replace('\\', '/');
        _progress.CurrentFile = fileName;
        ReportProgress();

        if (file.Flags.HasFlag(EDepotFileFlag.Directory))
        {
            Directory.CreateDirectory(Path.Combine(_gameDir, fileName));
            return;
        }

        var filePath = Path.Combine(_gameDir, fileName);
        var fileDir = Path.GetDirectoryName(filePath);
        if (fileDir != null)
            Directory.CreateDirectory(fileDir);

        // Validate existing file against manifest SHA-1 hash. A size-only check
        // would miss corruption from interrupted writes.
        if (File.Exists(filePath) && VerifyFileHash(filePath, file))
        {
            Interlocked.Add(ref _progress.DownloadedBytes, (long)file.TotalSize);
            ReportProgress();
            return;
        }

        var tempPath = filePath + ".downloading";
        var expectedLength = (long)file.TotalSize;
        var hadPartial = File.Exists(tempPath);

        if (hadPartial && new FileInfo(tempPath).Length != expectedLength)
        {
            File.Delete(tempPath);
            hadPartial = false;
        }

        var chunks = file.Chunks.OrderBy(c => c.Offset).ToList();
        var pending = chunks;
        var handle = File.OpenHandle(
            tempPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite
        );

        try
        {
            if (RandomAccess.GetLength(handle) != expectedLength)
                RandomAccess.SetLength(handle, expectedLength);

            if (hadPartial)
            {
                pending = chunks.Where(chunk => !ChunkMatchesOnDisk(handle, chunk)).ToList();
                if (pending.Count < chunks.Count)
                {
                    var pendingSet = new HashSet<DepotManifest.ChunkData>(pending);
                    var reusedBytes = chunks
                        .Where(chunk => !pendingSet.Contains(chunk))
                        .Sum(chunk => (long)chunk.UncompressedLength);
                    Log(
                        $"[{fileName}] resuming: {chunks.Count - pending.Count}/{chunks.Count} "
                            + $"chunks already on disk ({FormatSize(reusedBytes)})"
                    );
                    Interlocked.Add(ref _progress.DownloadedBytes, reusedBytes);
                    ReportProgress();
                }
            }

            if (pending.Count > 0)
            {
                using var semaphore = new SemaphoreSlim(ChunkConcurrency);
                var failures = new List<Exception>();
                var tasks = new List<Task>();

                foreach (var chunk in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    await semaphore.WaitAsync(ct);

                    tasks.Add(
                        Task.Run(
                            async () =>
                            {
                                try
                                {
                                    var buffer = await FetchChunkAsync(depotId, chunk, depotKey, ct);
                                    RandomAccess.Write(handle, buffer.AsSpan(), (long)chunk.Offset);
                                    Interlocked.Add(
                                        ref _progress.DownloadedBytes,
                                        buffer.Length
                                    );
                                    ReportProgress();
                                    LogProgressIfDue();
                                }
                                catch (Exception ex)
                                {
                                    lock (failures)
                                        failures.Add(ex);
                                }
                                finally
                                {
                                    semaphore.Release();
                                }
                            },
                            ct
                        )
                    );
                }

                await Task.WhenAll(tasks);

                if (failures.Count > 0)
                {
                    throw new Exception(
                        $"{failures.Count} chunk(s) failed for {fileName}: {failures[0].Message}"
                    );
                }
            }
        }
        finally
        {
            handle.Dispose();
        }

        // Verify the completed file before committing it.
        if (!VerifyFileHash(tempPath, file))
        {
            File.Delete(tempPath);
            throw new Exception($"SHA-1 verification failed for {fileName} after download");
        }

        File.Move(tempPath, filePath, overwrite: true);
    }

    // Fetches and decompresses a single chunk. A stalled range request is treated as
    // a failure after ChunkTimeoutSeconds and retried on another CDN host, so one
    // dead server cannot hang the whole download forever.
    private async Task<byte[]> FetchChunkAsync(
        uint depotId,
        DepotManifest.ChunkData chunk,
        byte[] depotKey,
        CancellationToken ct
    )
    {
        var server = GetNextServer();
        Exception lastError = null;

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var buffer = new byte[chunk.UncompressedLength];
            var request = _cdnClient.DownloadDepotChunkAsync(
                depotId,
                chunk,
                server,
                buffer,
                depotKey
            );
            var finished = await Task.WhenAny(
                request,
                Task.Delay(TimeSpan.FromSeconds(ChunkTimeoutSeconds), ct)
            );

            if (finished != request)
            {
                // The abandoned request still owns its buffer; leave it alone.
                _ = request.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                lastError = new TimeoutException(
                    $"chunk at offset {chunk.Offset} timed out after {ChunkTimeoutSeconds}s"
                );
                Log($"Chunk timeout at offset {chunk.Offset} ({server.Host}); retrying");
                server = GetNextServer();
                continue;
            }

            try
            {
                var written = await request;
                if (!VerifyChunkHash(buffer, written, chunk))
                {
                    lastError = new Exception($"chunk SHA-1 mismatch at offset {chunk.Offset}");
                    Log($"Chunk SHA-1 mismatch at offset {chunk.Offset}; retrying");
                    server = GetNextServer();
                    continue;
                }

                if (written != buffer.Length)
                    Array.Resize(ref buffer, written);
                return buffer;
            }
            catch (SteamKitWebRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                var token = await GetCdnAuthToken(depotId, server);
                if (token == null)
                {
                    lastError = ex;
                    Log($"Chunk forbidden and no CDN token for {server.Host}; retrying");
                    server = GetNextServer();
                    continue;
                }

                var authedBuffer = new byte[chunk.UncompressedLength];
                try
                {
                    var written = await _cdnClient.DownloadDepotChunkAsync(
                        depotId,
                        chunk,
                        server,
                        authedBuffer,
                        depotKey,
                        cdnAuthToken: token
                    );
                    if (!VerifyChunkHash(authedBuffer, written, chunk))
                    {
                        lastError = new Exception(
                            $"chunk SHA-1 mismatch at offset {chunk.Offset}"
                        );
                        server = GetNextServer();
                        continue;
                    }

                    if (written != authedBuffer.Length)
                        Array.Resize(ref authedBuffer, written);
                    return authedBuffer;
                }
                catch (Exception inner)
                {
                    lastError = inner;
                    Log($"Chunk download failed with token: {inner.Message}");
                    server = GetNextServer();
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log($"Chunk download failed (attempt {attempt + 1}): {ex.Message}");
                server = GetNextServer();
            }
        }

        throw new Exception(
            $"Failed to download chunk at offset {chunk.Offset} after {MaxRetries} attempts: "
                + $"{lastError?.Message}"
        );
    }

    // Re-hashes a chunk already present in a partial temp file.
    private static bool ChunkMatchesOnDisk(SafeFileHandle handle, DepotManifest.ChunkData chunk)
    {
        if (chunk.ChunkID == null || chunk.ChunkID.Length == 0 || chunk.UncompressedLength == 0)
            return false;

        var buffer = new byte[chunk.UncompressedLength];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = RandomAccess.Read(handle, buffer.AsSpan(read), (long)chunk.Offset + read);
            if (n <= 0)
                return false;
            read += n;
        }

        var hash = System.Security.Cryptography.SHA1.HashData(buffer);
        return hash.AsSpan().SequenceEqual(chunk.ChunkID);
    }

    private void LogProgressIfDue()
    {
        var downloaded = Interlocked.Read(ref _progress.DownloadedBytes);
        if (downloaded - _lastProgressLog < ProgressLogIntervalBytes)
            return;
        _lastProgressLog = downloaded;

        var elapsed = DateTime.UtcNow - _downloadStartUtc;
        var speed = elapsed.TotalSeconds > 0 ? downloaded / elapsed.TotalSeconds : 0;
        Log(
            $"{FormatSize(downloaded)} / {FormatSize(_progress.TotalBytes)} "
                + $"({_progress.Percentage:F1}%), {FormatSize((long)speed)}/s"
        );
    }

    // Computes SHA-1 of a decompressed chunk and compares it to the manifest ChunkID.
    private static bool VerifyChunkHash(byte[] buffer, int length, DepotManifest.ChunkData chunk)
    {
        if (chunk.ChunkID == null || chunk.ChunkID.Length == 0)
            return true;

        var hash = System.Security.Cryptography.SHA1.HashData(buffer.AsSpan(0, length));
        return hash.AsSpan().SequenceEqual(chunk.ChunkID);
    }

    // Computes SHA-1 of a file on disk and compares it to the manifest hash.
    private static bool VerifyFileHash(string path, DepotManifest.FileData file)
    {
        try
        {
            var info = new FileInfo(path);
            if ((ulong)info.Length != file.TotalSize)
                return false;

            // The PCK is rewritten in place to drop the Sentry autoload, which breaks
            // its manifest hash forever. Accept our own patched copy instead of
            // re-downloading a 1.4 GB file on every update check.
            if (IsPatchedPckAccepted(path, info))
                return true;

            using var fs = File.OpenRead(path);
            var hash = System.Security.Cryptography.SHA1.HashData(fs);
            return hash.AsSpan().SequenceEqual(file.FileHash);
        }
        catch
        {
            return false;
        }
    }

    // Marker written next to the PCK after in-place patching: size + mtime are enough
    // to tell "our patched copy" from "stale or half-written file" without hashing it.
    private static string PatchMarkerPath(string path) => path + ".patched";

    private static bool IsPatchedPckAccepted(string path, FileInfo info)
    {
        try
        {
            var marker = PatchMarkerPath(path);
            if (!File.Exists(marker))
                return false;

            var lines = File.ReadAllLines(marker);
            if (lines.Length < 3 || lines[0] != "v1")
                return false;

            return lines[1] == info.Length.ToString()
                && lines[2] == info.LastWriteTimeUtc.Ticks.ToString();
        }
        catch
        {
            return false;
        }
    }

    // Builds the list of files that need downloading. For manifest changes, uses
    // the hash diff. For all files in the target manifest, verifies the on-disk
    // copy against the expected SHA-1 — catching corruption from interrupted
    // writes, disk errors, or missing files.
    private List<DepotManifest.FileData> GetFilesNeedingDownload(
        DepotManifest oldManifest,
        DepotManifest newManifest,
        bool isUpdate
    )
    {
        var oldFiles = oldManifest?.Files.ToDictionary(f => f.FileName);
        var result = new List<DepotManifest.FileData>();
        int verified = 0;
        int corrupt = 0;

        foreach (var file in newManifest.Files)
        {
            if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                continue;

            // Manifest changed for this file — always re-download.
            if (isUpdate && oldFiles != null)
            {
                if (
                    !oldFiles.TryGetValue(file.FileName, out var oldFile)
                    || !file.FileHash.SequenceEqual(oldFile.FileHash)
                )
                {
                    result.Add(file);
                    continue;
                }
            }

            // Verify on-disk file matches the manifest hash.
            var filePath = Path.Combine(_gameDir, file.FileName.Replace('\\', '/'));
            if (VerifyFileHash(filePath, file))
            {
                verified++;
            }
            else
            {
                if (File.Exists(filePath))
                {
                    corrupt++;
                    Log($"File needs re-download (hash mismatch): {file.FileName}");
                }
                result.Add(file);
            }
        }

        if (verified > 0)
            Log($"Verified {verified} existing files");
        if (corrupt > 0)
            Log($"Found {corrupt} corrupt files requiring re-download");

        return result;
    }

    private static List<string> GetFilesToDelete(
        DepotManifest oldManifest,
        DepotManifest newManifest
    )
    {
        if (oldManifest == null)
            return new List<string>();

        var newFiles = new HashSet<string>(newManifest.Files.Select(f => f.FileName));
        return oldManifest
            .Files.Where(f => !newFiles.Contains(f.FileName))
            .Select(f => f.FileName)
            .ToList();
    }

    // Manifest state is tracked per branch so switching between public and
    // beta doesn't confuse update detection or the file diff.
    private static string SanitizeBranch(string branch) =>
        string.Concat(branch.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    private string ManifestIdPath(uint depotId) =>
        Path.Combine(_stateDir, $"{depotId}.{SanitizeBranch(Branch)}.id");

    private string ManifestPath(uint depotId) =>
        Path.Combine(_stateDir, $"{depotId}.{SanitizeBranch(Branch)}.manifest");

    private ulong LoadCachedManifestId(uint depotId)
    {
        var path = ManifestIdPath(depotId);
        if (!File.Exists(path))
        {
            // Builds predating per-branch state stored the public manifest
            // under the bare depot id; pick it up instead of re-verifying.
            if (Branch == PublicBranch)
            {
                var legacy = Path.Combine(_stateDir, $"{depotId}.id");
                if (File.Exists(legacy))
                    path = legacy;
                else
                    return 0;
            }
            else
            {
                return 0;
            }
        }

        return ulong.TryParse(File.ReadAllText(path).Trim(), out var id) ? id : 0;
    }

    private DepotManifest LoadCachedManifest(uint depotId)
    {
        var path = ManifestPath(depotId);
        if (!File.Exists(path))
        {
            if (Branch == PublicBranch)
            {
                var legacy = Path.Combine(_stateDir, $"{depotId}.manifest");
                if (File.Exists(legacy))
                    path = legacy;
                else
                    return null;
            }
            else
            {
                return null;
            }
        }

        try
        {
            using var fs = File.OpenRead(path);
            return DepotManifest.Deserialize(fs);
        }
        catch
        {
            return null;
        }
    }

    private void SaveManifest(uint depotId, DepotManifest manifest, ulong manifestId)
    {
        using (var fs = File.Create(ManifestPath(depotId)))
        {
            manifest.Serialize(fs);
        }
        File.WriteAllText(ManifestIdPath(depotId), manifestId.ToString());
    }

    private void Log(string msg)
    {
        PatchHelper.Log($"[Depot] {msg}");
        LogMessage?.Invoke(msg);
    }

    private void ReportProgress()
    {
        ProgressChanged?.Invoke(_progress);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    // Patches the PCK in-place to disable the Sentry autoload and GDExtension
    // entries (no android.arm64 build exists for the Sentry plugin).
    public static void PatchGamePck(string pckPath)
    {
        if (!File.Exists(pckPath))
            return;

        try
        {
            using var fs = new FileStream(pckPath, FileMode.Open, FileAccess.ReadWrite);
            using var reader = new BinaryReader(fs);

            uint magic = reader.ReadUInt32();
            if (magic != 0x43504447) // "GDPC"
                return;

            uint formatVersion = reader.ReadUInt32();
            reader.ReadUInt32(); // major
            reader.ReadUInt32(); // minor
            reader.ReadUInt32(); // patch
            uint flags = reader.ReadUInt32();
            long fileBase = reader.ReadInt64();
            long dirBase = reader.ReadInt64();
            fs.Seek(16 * 4, SeekOrigin.Current); // 16 reserved uint32s

            bool relativeOffsets = (flags & 0x02) != 0;

            fs.Position = dirBase;
            uint fileCount = reader.ReadUInt32();
            bool patched = false;

            for (uint i = 0; i < fileCount; i++)
            {
                uint pathLen = reader.ReadUInt32();
                byte[] pathBytes = reader.ReadBytes((int)pathLen);
                string path = System.Text.Encoding.UTF8.GetString(pathBytes).TrimEnd('\0');
                long offset = reader.ReadInt64();
                long size = reader.ReadInt64();
                reader.ReadBytes(16); // MD5
                reader.ReadUInt32(); // flags

                long absOffset = relativeOffsets ? fileBase + offset : offset;

                if (path == "res://project.godot")
                    patched |= PatchProjectGodot(fs, absOffset, size);
                else if (path == "res://.godot/extension_list.cfg")
                    patched |= PatchExtensionList(fs, absOffset, size);
            }

            if (patched)
            {
                var info = new FileInfo(pckPath);
                info.Refresh();
                File.WriteAllLines(
                    PatchMarkerPath(pckPath),
                    new[]
                    {
                        "v1",
                        info.Length.ToString(),
                        info.LastWriteTimeUtc.Ticks.ToString(),
                        "sentry-autoload-removed",
                    }
                );
                PatchHelper.Log("Patched game PCK: removed Sentry plugin references");
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"PCK patching failed (non-fatal): {ex.Message}");
        }
    }

    private static bool PatchProjectGodot(FileStream fs, long offset, long size)
    {
        long savedPos = fs.Position;
        fs.Position = offset;
        var content = new byte[size];
        fs.ReadExactly(content, 0, (int)size);

        // Comment out the Sentry autoload line by replacing 'S' with ';'.
        var search = System.Text.Encoding.UTF8.GetBytes(
            "SentryInit=\"*res://addons/sentry/SentryInit.gd\""
        );
        int idx = FindBytes(content, search);
        if (idx < 0)
        {
            fs.Position = savedPos;
            return false;
        }

        content[idx] = (byte)';';
        fs.Position = offset;
        fs.Write(content, 0, content.Length);
        fs.Position = savedPos;
        return true;
    }

    private static bool PatchExtensionList(FileStream fs, long offset, long size)
    {
        long savedPos = fs.Position;
        fs.Position = offset;
        var content = new byte[size];
        fs.ReadExactly(content, 0, (int)size);

        // Overwrite the Sentry GDExtension path with spaces (same byte count).
        var search = System.Text.Encoding.UTF8.GetBytes("res://addons/sentry/sentry.gdextension");
        int idx = FindBytes(content, search);
        if (idx < 0)
        {
            fs.Position = savedPos;
            return false;
        }

        for (int i = 0; i < search.Length; i++)
            content[idx + i] = (byte)' ';

        fs.Position = offset;
        fs.Write(content, 0, content.Length);
        fs.Position = savedPos;
        return true;
    }

    private static int FindBytes(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    public void Dispose()
    {
        _cdnClient?.Dispose();
    }
}