using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Net.Http;

namespace PCL.Core.Test.Network;

[TestClass]
public class DohConnection
{
    [TestMethod]
    [TestCategory("ExternalNetwork")]
    public async Task TestDohConnection()
    {
        if (Environment.GetEnvironmentVariable("PCL_TEST_NETWORK") != "1")
            Assert.Inconclusive("需要外网 HTTPS；设置 PCL_TEST_NETWORK=1 运行该集成测试。");

        using var client = new HttpClient(HostConnectionHandler.Instance.CreateHttpHandler(new SocketsHttpHandler()))
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.cloudflare.com/cdn-cgi/trace")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        using var response = await client.SendAsync(request, TestContext.CancellationToken);
        Assert.IsTrue(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        Assert.IsFalse(string.IsNullOrWhiteSpace(body));
    }

    public TestContext TestContext { get; set; }
}
