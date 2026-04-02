using System.Net;
using System.Text;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Kube;

namespace Kuberkynesis.Agent.Tests;

public sealed class PrometheusMetricsSourceTests
{
    [Fact]
    public async Task QueryPodUsageAsync_ReturnsCpuAndMemoryValuesFromPrometheus()
    {
        var httpClient = new HttpClient(new StubHandler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? string.Empty);

            if (query.Contains("container_cpu_usage_seconds_total", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateResponse("""
                    {
                      "status": "success",
                      "data": {
                        "resultType": "vector",
                        "result": [
                          { "metric": { "pod": "orders-api-0" }, "value": [ 1711785600.0, "180" ] },
                          { "metric": { "pod": "checkout-api-0" }, "value": [ 1711785600.0, "95" ] }
                        ]
                      }
                    }
                    """));
            }

            return Task.FromResult(CreateResponse("""
                {
                  "status": "success",
                  "data": {
                    "resultType": "vector",
                    "result": [
                      { "metric": { "pod": "orders-api-0" }, "value": [ 1711785600.0, "100663296" ] },
                      { "metric": { "pod": "checkout-api-0" }, "value": [ 1711785600.0, "50331648" ] }
                    ]
                  }
                }
                """));
        }))
        {
            BaseAddress = new Uri("http://prometheus:9090/", UriKind.Absolute)
        };

        var source = new PrometheusMetricsSource(
            httpClient,
            new AgentRuntimeOptions
            {
                Metrics = new MetricsOptions
                {
                    Prometheus = new PrometheusMetricsOptions
                    {
                        Enabled = true,
                        BaseUrl = "http://prometheus:9090/",
                        CpuRateWindow = "5m"
                    }
                }
            });

        var result = await source.QueryPodUsageAsync(
            "orders-prod",
            ["orders-api-0", "checkout-api-0"],
            CancellationToken.None);

        Assert.True(result.MetricsAvailable);
        Assert.Equal("Prometheus 5m rate", result.Window);
        Assert.Equal(180, result.UsageByPod["orders-api-0"].CpuMillicores);
        Assert.Equal(95, result.UsageByPod["checkout-api-0"].CpuMillicores);
        Assert.Equal(100663296, result.UsageByPod["orders-api-0"].MemoryBytes);
        Assert.Equal(50331648, result.UsageByPod["checkout-api-0"].MemoryBytes);
    }

    [Fact]
    public async Task QueryPodUsageAsync_ReturnsDisabledWhenFallbackIsNotConfigured()
    {
        var source = new PrometheusMetricsSource(
            new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
            new AgentRuntimeOptions());

        var result = await source.QueryPodUsageAsync(
            "orders-prod",
            ["orders-api-0"],
            CancellationToken.None);

        Assert.False(result.MetricsAvailable);
        Assert.Null(result.FailureMessage);
        Assert.Empty(result.UsageByPod);
    }

    private static HttpResponseMessage CreateResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> handler;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }
}
