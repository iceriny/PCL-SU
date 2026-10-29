using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Profile.Authentication;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public class MinecraftServiceExceptionTest
{
    private const string ClientId = "11111111-2222-4333-8444-555555555555";
    private const string RequestId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";

    [TestMethod]
    [DataRow("ForbiddenOperationException", "Invalid app registration")]
    [DataRow("InvalidAppRegistration", null)]
    public async Task LoginWithXboxForbiddenExplicitRegistrationRejectionPreservesDiagnostics(
        string serviceError, string? reason)
    {
        using var response = JsonResponse(HttpStatusCode.Forbidden, new
        {
            error = serviceError,
            errorMessage = reason,
            path = "/authentication/login_with_xbox",
            requestId = RequestId
        });

        var error = await MinecraftServiceException.FromResponseAsync(response,
            MinecraftServiceOperation.LoginWithXbox, CancellationToken.None, ClientId);

        Assert.IsTrue(error.IsAppRegistrationRejected);
        Assert.AreEqual(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.AreEqual(MinecraftServiceOperation.LoginWithXbox, error.Operation);
        Assert.AreEqual(serviceError, error.ServiceError);
        Assert.AreEqual(reason, error.ServiceMessage);
        Assert.AreEqual("/authentication/login_with_xbox", error.ServicePath);
        Assert.AreEqual(RequestId, error.RequestId);
        Assert.AreEqual(ClientId, error.ClientId);
        Assert.AreEqual("application/json", error.ResponseContentType);
        Assert.IsFalse(error.ErrorBodyTruncated);
    }

    [TestMethod]
    public async Task EnsureSuccessThrowsOrdinaryForbiddenWithoutRegistrationClassification()
    {
        using var response = JsonResponse(HttpStatusCode.Forbidden, new
        {
            error = "ForbiddenOperationException",
            errorMessage = "Access denied"
        });

        var error = await Assert.ThrowsExactlyAsync<MinecraftServiceException>(() =>
            MinecraftServiceException.EnsureSuccessAsync(response, MinecraftServiceOperation.LoginWithXbox,
                CancellationToken.None, ClientId));

        Assert.IsFalse(error.IsAppRegistrationRejected);
        Assert.AreEqual(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.AreEqual(MinecraftServiceOperation.LoginWithXbox, error.Operation);
        Assert.AreEqual("Access denied", error.ServiceMessage);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, MinecraftServiceOperation.LoginWithXbox)]
    [DataRow(HttpStatusCode.Forbidden, MinecraftServiceOperation.Entitlements)]
    [DataRow(HttpStatusCode.Forbidden, MinecraftServiceOperation.Profile)]
    public async Task StatusAndOperationRemainIndependent(HttpStatusCode status, MinecraftServiceOperation operation)
    {
        using var response = JsonResponse(status, new { errorMessage = "Invalid app registration" });

        var error = await MinecraftServiceException.FromResponseAsync(response, operation, CancellationToken.None);

        Assert.AreEqual(status, error.StatusCode);
        Assert.AreEqual(operation, error.Operation);
        Assert.IsFalse(error.IsAppRegistrationRejected);
    }

    [TestMethod]
    [DataRow("errorMessage")]
    [DataRow("message")]
    [DataRow("developerMessage")]
    public async Task ReadsSupportedBackendMessageFields(string field)
    {
        using var response = JsonResponse(HttpStatusCode.Forbidden, new Dictionary<string, string>
        {
            ["error"] = "ForbiddenOperationException",
            [field] = "Backend reason"
        });

        var error = await MinecraftServiceException.FromResponseAsync(response,
            MinecraftServiceOperation.LoginWithXbox, CancellationToken.None);

        Assert.AreEqual("Backend reason", error.ServiceMessage);
        Assert.IsFalse(error.IsAppRegistrationRejected);
    }

    [TestMethod]
    public async Task RedactsCredentialsFromBackendDescriptionAndOmitsOtherJsonFields()
    {
        const string fakeJwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJmYWtlLXRlc3QtdXNlciJ9.ZmFrZS1zaWduYXR1cmU";
        const string fakeJweEmptyKey = "eyJhbGciOiJkaXIiLCJlbmMiOiJmYWtlIn0.." +
                                       "fake-jwe-iv.fake-jwe-ciphertext.fake-empty-jwe-tail";
        const string fakeJweEncryptedKey = "eyJhbGciOiJmYWtlIiwiZW5jIjoiZmFrZSJ9.fake-encrypted-key." +
                                           "fake-jwe-iv.fake-jwe-ciphertext.fake-nonempty-jwe-tail";
        using var response = JsonResponse(HttpStatusCode.Forbidden, new
        {
            error = "ForbiddenOperationException",
            errorMessage = "Rejected\r\nBearer fake-bearer-value " + fakeJwt + " " + fakeJweEmptyKey + " " +
                           fakeJweEncryptedKey +
                           " XBL3.0 x=fake-user-hash;fake-xbl-token " +
                           "access_token=fake-access-token refresh_token=fake-refresh-token " +
                           "client_secret=fake-client-secret password=fake-password",
            path = "/authentication/login_with_xbox",
            access_token = "body-access-value",
            refresh_token = "body-refresh-value",
            identityToken = "body-identity-value",
            debug = new { value = "body-debug-value" }
        });

        var error = await MinecraftServiceException.FromResponseAsync(response,
            MinecraftServiceOperation.LoginWithXbox, CancellationToken.None, ClientId);

        Assert.IsNotNull(error.ServiceMessage);
        StringAssert.Contains(error.ServiceMessage, "Rejected");
        var diagnostics = string.Join("|", error.Message, error.ServiceError, error.ServiceMessage,
            error.ServicePath, error.RequestId, error.ClientId);
        foreach (var secret in new[]
                 {
                     fakeJwt, fakeJweEmptyKey, fakeJweEncryptedKey, "fake-empty-jwe-tail", "fake-nonempty-jwe-tail",
                     "fake-bearer-value", "fake-user-hash", "fake-xbl-token", "fake-access-token",
                     "fake-refresh-token", "fake-client-secret", "fake-password", "body-access-value",
                     "body-refresh-value", "body-identity-value", "body-debug-value"
                 })
            Assert.IsFalse(diagnostics.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(error.ServiceMessage.Contains('\r'));
        Assert.IsFalse(error.ServiceMessage.Contains('\n'));
        Assert.IsFalse(error.ServiceError?.Contains('\r') == true);
        Assert.IsFalse(error.ServiceError?.Contains('\n') == true);
    }

    [TestMethod]
    public async Task IgnoresClientAndRequestIdsThatAreNotGuids()
    {
        using var response = JsonResponse(HttpStatusCode.Forbidden, new
        {
            errorMessage = "Access denied",
            requestId = "fake-request-secret"
        });

        var error = await MinecraftServiceException.FromResponseAsync(response,
            MinecraftServiceOperation.LoginWithXbox, CancellationToken.None, "fake-client-secret");

        Assert.IsNull(error.ClientId);
        Assert.IsNull(error.RequestId);
        Assert.IsFalse(error.Message.Contains("fake-request-secret", StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains("fake-client-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HtmlFailureDoesNotIncludeResponseBody()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<html><p>opaque-html-private-marker</p>" +
                                        "<p>Invalid app registration Bearer fake-html-secret</p></html>",
                Encoding.UTF8, "text/html")
        };

        var error = await MinecraftServiceException.FromResponseAsync(response,
            MinecraftServiceOperation.LoginWithXbox, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.AreEqual("text/html", error.ResponseContentType);
        Assert.IsNull(error.ServiceError);
        Assert.IsNull(error.ServiceMessage);
        Assert.IsNull(error.ServicePath);
        Assert.IsFalse(error.IsAppRegistrationRejected);
        Assert.IsFalse(error.Message.Contains("opaque-html-private-marker", StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains("fake-html-secret", StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains("<html>", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OversizedJsonIsBoundedAndDoesNotClassifyItsTruncatedPrefix()
    {
        var json = "{\"errorMessage\":\"Invalid app registration\",\"padding\":\"" +
                   new string('x', 20 * 1024) + "\"}";
        using var body = new CountingReadStream(Encoding.UTF8.GetBytes(json));
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StreamContent(body)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var error = await MinecraftServiceException.FromResponseAsync(response,
            MinecraftServiceOperation.LoginWithXbox, CancellationToken.None);

        Assert.IsTrue(error.ErrorBodyTruncated);
        Assert.IsFalse(error.IsAppRegistrationRejected);
        Assert.AreEqual(HttpStatusCode.Forbidden, error.StatusCode);
        // One lookahead byte is sufficient to distinguish exactly 16 KiB from a longer body.
        Assert.IsLessThanOrEqualTo(16 * 1024 + 1, body.BytesRead);
    }

    [TestMethod]
    public async Task CancellationWhileReadingErrorBodyPropagates()
    {
        using var body = new CancellationReadStream();
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StreamContent(body)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var cancellation = new CancellationTokenSource();
        var pending = MinecraftServiceException.FromResponseAsync(response, MinecraftServiceOperation.LoginWithXbox,
            cancellation.Token);
        await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    public async Task EnsureSuccessDoesNotReadSuccessfulResponseBody()
    {
        using var body = new ThrowOnReadStream();
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };

        await MinecraftServiceException.EnsureSuccessAsync(response, MinecraftServiceOperation.Profile,
            CancellationToken.None, ClientId);

        Assert.AreEqual(0, body.ReadAttempts);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private abstract class ReadOnlyTestStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("This test stream requires asynchronous reads.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class CountingReadStream(byte[] content) : ReadOnlyTestStream
    {
        public int BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, content.Length - BytesRead);
            content.AsMemory(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class CancellationReadStream : ReadOnlyTestStream
    {
        private readonly CancellationTokenSource _disposed = new();
        private bool _isDisposed;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, linked.Token);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_isDisposed)
            {
                _isDisposed = true;
                _disposed.Cancel();
                _disposed.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowOnReadStream : ReadOnlyTestStream
    {
        public int ReadAttempts { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadAttempts++;
            throw new InvalidOperationException("A successful response body must remain unread.");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadAttempts++;
            throw new InvalidOperationException("A successful response body must remain unread.");
        }
    }
}
