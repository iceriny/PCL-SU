using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Profile.Authentication;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class MinecraftServiceExceptionTest
{
    private const string RequestId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, MinecraftServiceOperation.LoginWithXbox, "Invalid app registration", true)]
    [DataRow(HttpStatusCode.Forbidden, MinecraftServiceOperation.LoginWithXbox, "Access denied", false)]
    [DataRow(HttpStatusCode.Unauthorized, MinecraftServiceOperation.LoginWithXbox, "Invalid app registration", false)]
    [DataRow(HttpStatusCode.Forbidden, MinecraftServiceOperation.Entitlements, "Invalid app registration", false)]
    public async Task ClassifiesOnlyExplicitAppRegistrationDenials(
        HttpStatusCode status, MinecraftServiceOperation operation, string reason, bool registrationRejected)
    {
        using var response = JsonResponse(status, new
        {
            error = "ForbiddenOperationException",
            errorMessage = reason,
            requestId = RequestId
        });

        var error = await Assert.ThrowsExactlyAsync<MinecraftServiceException>(() =>
            MinecraftServiceException.EnsureSuccessAsync(response, operation, CancellationToken.None));

        Assert.AreEqual(status, error.StatusCode);
        Assert.AreEqual(operation, error.Operation);
        Assert.AreEqual(registrationRejected, error.IsAppRegistrationRejected);
        Assert.AreEqual(reason, error.ServiceMessage);
        Assert.AreEqual(RequestId, error.RequestId);
    }

    [TestMethod]
    public async Task RedactsCredentialValuesAndIgnoresUnrecognizedFields()
    {
        const string jwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJmYWtlIn0.ZmFrZS1zaWduYXR1cmU";
        using var response = JsonResponse(HttpStatusCode.Forbidden, new
        {
            errorMessage = $"Rejected\r\nBearer fake-bearer {jwt} XBL3.0 x=fake-user-hash;fake-xbl access_token=fake-access",
            path = "/authentication/login_with_xbox?token=fake-query",
            access_token = "fake-body-token"
        });

        var error = await MinecraftServiceException.FromResponseAsync(response,
            MinecraftServiceOperation.LoginWithXbox, CancellationToken.None);

        Assert.AreEqual("/authentication/login_with_xbox", error.ServicePath);
        Assert.Contains("Rejected", error.ServiceMessage!);
        Assert.DoesNotContain("\n", error.ServiceMessage!);
        foreach (var secret in new[] { jwt, "fake-bearer", "fake-user-hash", "fake-xbl", "fake-access", "fake-query", "fake-body-token" })
            Assert.DoesNotContain(secret, error.Message);
    }

    [TestMethod]
    public async Task DiscardsOversizedDiagnosticBodies()
    {
        using var response = JsonResponse(HttpStatusCode.Forbidden, new
        {
            errorMessage = "Invalid app registration " + new string('x', 16 * 1024)
        });

        var error = await MinecraftServiceException.FromResponseAsync(response,
            MinecraftServiceOperation.LoginWithXbox, CancellationToken.None);

        Assert.IsTrue(error.ErrorBodyTruncated);
        Assert.IsFalse(error.IsAppRegistrationRejected);
        Assert.IsNull(error.ServiceMessage);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }
}
