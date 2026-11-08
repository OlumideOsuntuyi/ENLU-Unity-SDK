#nullable enable
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Enlu
{
    /// <summary>
    /// The default transport: <c>UnityWebRequest</c> wrapped in a <see cref="TaskCompletionSource{TResult}"/>.
    /// Works on every Unity platform including WebGL (no threads). Call from the main thread.
    /// </summary>
    public sealed class UnityWebRequestTransport : IEnluTransport
    {
        /// <inheritdoc />
        public Task<EnluHttpResponse> SendAsync(EnluHttpRequest request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<EnluHttpResponse>(cancellationToken);

            var tcs = new TaskCompletionSource<EnluHttpResponse>();
            var www = new UnityWebRequest(request.Url, request.Method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Math.Max(1, request.TimeoutSeconds),
            };
            if (request.Body != null)
            {
                www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.Body)) { contentType = "application/json" };
            }
            foreach (var h in request.Headers)
            {
                www.SetRequestHeader(h.Key, h.Value);
            }

            var registration = default(CancellationTokenRegistration);
            var aborted = false;
            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(() =>
                {
                    void Abort()
                    {
                        aborted = true;
                        try
                        {
                            www.Abort();
                        }
                        catch (Exception)
                        {
                            // Already finished or disposed.
                        }
                        tcs.TrySetCanceled(cancellationToken);
                    }

                    if (EnluRunner.IsMainThread) Abort();
                    else EnluRunner.Post(Abort);
                });
            }

            UnityWebRequestAsyncOperation op;
            try
            {
                op = www.SendWebRequest();
            }
            catch (Exception e)
            {
                registration.Dispose();
                www.Dispose();
                return Task.FromResult(new EnluHttpResponse { Status = 0, NetworkError = e.Message });
            }

            op.completed += _ =>
            {
                registration.Dispose();
                try
                {
                    if (aborted)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }
                    tcs.TrySetResult(ReadResponse(www));
                }
                catch (Exception e)
                {
                    tcs.TrySetResult(new EnluHttpResponse { Status = 0, NetworkError = e.Message });
                }
                finally
                {
                    www.Dispose();
                }
            };
            return tcs.Task;
        }

        private static EnluHttpResponse ReadResponse(UnityWebRequest www)
        {
            var res = new EnluHttpResponse();
            if (www.result == UnityWebRequest.Result.ConnectionError || (www.responseCode == 0 && www.result != UnityWebRequest.Result.Success))
            {
                res.Status = 0;
                res.NetworkError = string.IsNullOrEmpty(www.error) ? "connection failed" : www.error;
                res.TimedOut = www.error != null && www.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
                return res;
            }
            res.Status = (int)www.responseCode;
            res.Body = www.downloadHandler != null ? (www.downloadHandler.text ?? string.Empty) : string.Empty;
            var headers = www.GetResponseHeaders();
            if (headers != null)
            {
                foreach (var kv in headers) res.Headers[kv.Key.ToLowerInvariant()] = kv.Value;
            }
            return res;
        }
    }

    /// <summary>Stores values in <c>PlayerPrefs</c> (plain text on the device).</summary>
    public sealed class PlayerPrefsStore : IEnluKeyValueStore
    {
        /// <inheritdoc />
        public string? Get(string key) => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;

        /// <inheritdoc />
        public void Set(string key, string value)
        {
            PlayerPrefs.SetString(key, value);
            PlayerPrefs.Save();
        }

        /// <inheritdoc />
        public void Delete(string key)
        {
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }
    }
}
