﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// Author: chgeuer@microsoft.com github.com/chgeuer

namespace embedder
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Microsoft.WindowsAzure.Storage.Blob;
    using Microsoft.WindowsAzure.Storage.Queue;
    using Newtonsoft.Json;
    using Polly;

    public class ExecutionResult
    {
        public bool Success { get; set; }

        public string Output { get; set; }
    }

    public static class Utils
    {
        private static readonly IImpl _impl = new AzureImpl();

        public static Task<ExecutionResult> DispatchMessage(this CloudQueue queue, INotificationMessage message) => _impl.DispatchMessage(queue, message);

        public static Task<ExecutionResult> DownloadToAsync(this Uri blobAbsoluteUri, FileInfo file, string prefix = "") => _impl.DownloadToAsync(blobAbsoluteUri, file, prefix);

        public static Task<ExecutionResult> UploadToAsync(this FileInfo file, Uri blobAbsoluteUri, string prefix = "") => _impl.UploadToAsync(file, blobAbsoluteUri, prefix);

        public static async Task<ExecutionResult> RunProcessAsync(
             string fileName, string[] arguments = null,
             IDictionary<string, string> additionalEnvironment = null, 
             string prefix = "")
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments != null ? string.Join(" ", arguments) : null,
                // UseShellExecute = false,
                // CreateNoWindow = true,
                WorkingDirectory = "."
            };
            if (additionalEnvironment != null)
            {
                foreach (var k in additionalEnvironment.Keys)
                {
                    processStartInfo.Environment.Add(k, additionalEnvironment[k]);
                }
            }

            try
            {
                return await _impl.RunProcessAsync(processStartInfo, prefix);
            }
            catch (Exception ex)
            {
                return new ExecutionResult
                {
                    Success = false,
                    Output = $"{prefix}: Exception {ex.Message}"
                };
            }
        }

        public static FileInfo AsSafeFileName(this string x)
        {
            var unsafeChars = new List<char>(Path.GetInvalidFileNameChars());
            unsafeChars.AddRange(new[] { '%', ' ' });
            foreach (var c in unsafeChars) { x = x.Replace(c, '_'); }
            return new FileInfo(x);
        }

        public static FileInfo AsLocalFile(this string filename) { return (filename).AsSafeFileName(); }

        public static FileInfo AsStatsFile(this string filename) { return filename.Replace(".mp4", ".stats").AsSafeFileName(); }

        public static FileInfo AsMmrkFile(this string filename) { return filename.Replace(".mp4", ".mmrk").AsSafeFileName(); }

        public static FileInfo AsWatermarkFileForUser(this string filename, string userid) { return (filename.Replace(".mp4", $"-{userid}.mp4")).AsSafeFileName(); }

        public static Uri AsUri(this string uri) { return string.IsNullOrEmpty(uri) ? null : new Uri(uri); }
    }

    public interface IImpl
    {
        Task<ExecutionResult> RunProcessAsync(ProcessStartInfo processStartInfo, string prefix);
        Task<ExecutionResult> DispatchMessage(CloudQueue queue, INotificationMessage message);
        Task<ExecutionResult> DownloadToAsync(Uri blobAbsoluteUri, FileInfo file, string prefix = "");
        Task<ExecutionResult> UploadToAsync(FileInfo file, Uri blobAbsoluteUri, string prefix = "");
    }

    internal class AzureImpl : IImpl
        {
            public Task<ExecutionResult> RunProcessAsync(ProcessStartInfo processStartInfo, string prefix)
            {
                var output = new List<string>();

                var process = new Process
                {
                    StartInfo = processStartInfo,
                    EnableRaisingEvents = true
                };
                processStartInfo.RedirectStandardOutput = true;
                processStartInfo.RedirectStandardError = true;
                process.OutputDataReceived += (sender, data) => { if (!string.IsNullOrEmpty(data.Data)) { output.Add($"{prefix}: {data.Data}"); } };
                process.ErrorDataReceived += (sender, data) => { if (!string.IsNullOrEmpty(data.Data)) { output.Add($"{prefix} ERR: {data.Data}"); } };

                Func<Task<ExecutionResult>> RunAsync = () =>
                {
                    var tcs = new TaskCompletionSource<ExecutionResult>();
                    process.Exited += (sender, args) =>
                    {
                        var o = string.Join("\n", output.ToArray());
                        tcs.SetResult(new ExecutionResult { Success = process.ExitCode == 0, Output = o });
                        process.Dispose();
                    };

                    process.Start();
                    if (process.StartInfo.RedirectStandardOutput) process.BeginOutputReadLine();
                    if (process.StartInfo.RedirectStandardError) process.BeginErrorReadLine();

                    return tcs.Task;
                };

                return RunAsync();
            }

            public async Task<ExecutionResult> DispatchMessage(CloudQueue queue, INotificationMessage message)
            {
                var prefix = "QUEUE";
                if (queue == null)
                {
                    return new ExecutionResult { Success = false, Output = $"{prefix}: ERR queue is null" };
                }
                try
                {
                    await queue.AddMessageAsync(new CloudQueueMessage(JsonConvert.SerializeObject(message)));

                    return new ExecutionResult { Success = true, Output = $"{prefix}: Sent message to {queue.Uri.AbsoluteUri}" };
                }
                catch (Exception ex)
                {
                    return new ExecutionResult { Success = false, Output = $"{prefix}: ERR {ex.Message} {queue.Uri.AbsoluteUri}" };
                }
            }

            public async Task<ExecutionResult> DownloadToAsync(Uri blobAbsoluteUri, FileInfo file, string prefix = "")
            {
                var retryResult = await Policy
                    .Handle<Exception>()
                    .WaitAndRetryAsync(
                        retryCount: 5,
                        sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)))
                    .ExecuteAndCaptureAsync(async () =>
                    {
                        using (var client = new HttpClient { /* Timeout = TimeSpan.FromMinutes() */ })
                        using (var stream = await client.GetStreamAsync(blobAbsoluteUri))
                        using (var output = file.OpenWrite())
                        {
                            await stream.CopyToAsync(output);
                        }

                        return new ExecutionResult
                        {
                            Success = true,
                            Output = $"{prefix}: Downloaded {blobAbsoluteUri.AbsoluteUri} to {file.FullName}"
                        };
                    });

                if (retryResult.Outcome == OutcomeType.Successful)
                {
                    return retryResult.Result;
                }
                else
                {
                    return new ExecutionResult
                    {
                        Success = false,
                        Output = $"{prefix}: ERR during download: \"{retryResult.FinalException.Message}\" {blobAbsoluteUri.AbsoluteUri}"
                    };
                }
            }

            public async Task<ExecutionResult> UploadToAsync(FileInfo file, Uri blobAbsoluteUri, string prefix = "")
            {
                try
                {
                    var blockBlob = new CloudBlockBlob(blobAbsoluteUri);

                    await LargeFileUploaderUtils.UploadAsync(file: file, blockBlob: blockBlob, uploadParallelism: 4);
                    // await blockBlob.UploadFromFileAsync(file.FullName);

                    return new ExecutionResult { Success = true, Output = $"{prefix}: Uploaded {file.FullName} to {blobAbsoluteUri.AbsoluteUri}" };
                }
                catch (Exception ex)
                {
                    return new ExecutionResult { Success = false, Output = $"{prefix}: ERR during upload: \"{ex.Message}\" {blobAbsoluteUri.AbsoluteUri}" };
                }
            }
        }

    //internal class StdoutImpl : IImpl
    //{
    //    private async Task<ExecutionResult> Msg(string msg)
    //    {
    //        // await Task.Delay(TimeSpan.FromSeconds(1));
    //        // Console.WriteLine(msg);
    //        return new ExecutionResult { Success = true, Output = msg };
    //    }

    //    public Task<ExecutionResult> DispatchMessage(CloudQueue queue, INotificationMessage message) 
    //        => Msg($"Dispatching {message} to {queue.Name}");

    //    public Task<ExecutionResult> DownloadToAsync(Uri blobAbsoluteUri, FileInfo file, string prefix = "")
    //        => Msg($"Download {blobAbsoluteUri.AbsoluteUri} to {file.FullName}.");

    //    public Task<ExecutionResult> RunProcessAsync(ProcessStartInfo processStartInfo, string prefix)
    //        => Msg($"Run {processStartInfo.FileName} {processStartInfo.Arguments}");

    //    public Task<ExecutionResult> UploadToAsync(FileInfo file, Uri blobAbsoluteUri, string prefix = "")
    //        => Msg($"Upload {file.FullName} to {blobAbsoluteUri.AbsoluteUri}");
    //}
}