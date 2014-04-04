namespace Owin.EmbeddedHost.Tests
{
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;

    public class OwinEmbeddedHostTests
    {
        [Fact]
        public async Task On_exception_then_should_get_status_code_500()
        {
            using(var host = OwinEmbeddedHost.Create(app => app.Run(ctx =>
            {
                throw new Exception("oops");
            })))
            {
                using (var httpClient = new HttpClient(new OwinHttpMessageHandler(host.Invoke)))
                {
                    var response = await httpClient.GetAsync("http://localhost/");

                    response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                }
            }
        }

        [Fact]
        public async Task Can_get_response()
        {
            using (var host = OwinEmbeddedHost.Create(app => app.Run(ctx =>
            {
                ctx.Response.StatusCode = 200;
                return Task.Delay(0);
            })))
            {
                using (var httpClient = new HttpClient(new OwinHttpMessageHandler(host.Invoke)))
                {
                    var response = await httpClient.GetAsync("http://localhost/");

                    response.StatusCode.Should().Be(HttpStatusCode.OK);
                }
            }
        }

    }
}