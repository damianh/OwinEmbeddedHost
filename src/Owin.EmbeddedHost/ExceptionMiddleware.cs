namespace Owin.EmbeddedHost
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.Owin;

    internal class ExceptionMiddleware
    {
        private readonly Func<IDictionary<string, object>, Task> _next;

        public ExceptionMiddleware(Func<IDictionary<string, object>, Task> next)
        {
            _next = next;
        }

        public async Task Invoke(IDictionary<string, object> environment)
        {
            try
            {
                await _next(environment);
            }
            catch (Exception)
            {
                var owinContext = new OwinContext(environment);
                owinContext.Response.StatusCode = 500;
            }
        }
    }
}