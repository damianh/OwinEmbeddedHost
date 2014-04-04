Owin.EmbeddedHost
=================

Library to allow you to host owin application in your process and invoke the owin pipeline directly.

### Example
```csharp
using(var host = OwinEmbeddedHost.Create<Startup>())
{
    await host.Invoke(enviroment);
}
```

Uses code from Micrsoft.Owin.Testing