// Adapted from Nashi (https://github.com/orkunisitmak/nashi-app),
// Copyright (c) 2026 Orkun Işıtmak and Nashi contributors, MIT License.
using System.Diagnostics;
using System.Text;

namespace PanDaF.Core;

public sealed record ProcessResult(string Output, string Error);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("Aracın tam yolu gerekli.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var value in arguments) start.ArgumentList.Add(value);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Dönüştürme aracı başlatılamadı.");
        process.StandardInput.Close();
        using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var results = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(results[1]) ? "Dönüştürme aracı hata verdi." : results[1][Math.Max(0, results[1].Length - 3000)..].Trim());
        return new(results[0], results[1]);
    }
}

public static class ToolInstallation
{
    public static bool UsableFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    public static void Publish(string stage, string target, Func<string, bool> complete)
    {
        if (!complete(stage)) throw new UserFacingException("İndirilen araçlar eksik. Lütfen tekrar deneyin.");
        if (complete(target)) return;
        var backup = Path.Combine(Path.GetDirectoryName(target)!, ".previous-" + Guid.NewGuid().ToString("N"));
        var existing = Directory.Exists(target);
        if (existing) Directory.Move(target, backup);
        try { Directory.Move(stage, target); }
        catch { if (existing) Directory.Move(backup, target); throw; }
        if (existing) { try { Directory.Delete(backup, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

public static class VerifiedDownload
{
    public static async Task FetchAsync(Uri uri, string output, long maximum, Action<double>? progress, CancellationToken token)
    {
        if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidDataException("HTTPS indirme gerekli.");
        using var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PanDaF/1.0");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { await FetchOnce(client, uri, output, maximum, progress, token, TimeSpan.FromSeconds(60)); return; }
            catch (Exception error) when (!token.IsCancellationRequested && attempt < 2 && error is HttpRequestException or EndOfStreamException or TimeoutException)
            { await Task.Delay(1000 * (attempt + 1), token); }
        }
    }

    private static async Task FetchOnce(HttpClient client, Uri uri, string output, long maximum, Action<double>? progress, CancellationToken token, TimeSpan idle)
    {
        var created = false;
        try
        {
            using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(token); inactivity.CancelAfter(idle);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, inactivity.Token);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException("Güvenli olmayan indirme yönlendirmesi.");
            var length = response.Content.Headers.ContentLength;
            if (length > maximum) throw new InvalidDataException("İndirilecek dosya beklenenden büyük.");
            await using var input = await response.Content.ReadAsStreamAsync(inactivity.Token);
            await using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true); created = true;
            var buffer = new byte[81920]; long total = 0;
            while (true)
            {
                inactivity.CancelAfter(idle);
                int count;
                try { count = await input.ReadAsync(buffer, inactivity.Token); }
                catch (IOException error) { throw new HttpRequestException("İndirme bağlantısı kesildi.", error); }
                if (count == 0) break;
                total += count;
                if (total > maximum) throw new InvalidDataException("İndirme sınırı aşıldı.");
                await target.WriteAsync(buffer.AsMemory(0, count), token);
                if (length > 0) progress?.Invoke((double)total / length.Value);
            }
            if (length is not null && total != length) throw new EndOfStreamException("İndirme yarıda kaldı.");
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            if (created) File.Delete(output);
            throw new TimeoutException("İndirme yanıt vermiyor. Lütfen tekrar deneyin.", error);
        }
        catch { if (created && File.Exists(output)) File.Delete(output); throw; }
    }
}
