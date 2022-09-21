// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.

using System.Threading.Tasks;
using Duende.Bff.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Duende.Bff.Endpoints;

/// <summary>
/// Middleware to provide anti-forgery protection via a static header and 302 to 401 conversion
/// Must run *before* the authorization middleware
/// </summary>
public class BffMiddleware
{
    private readonly RequestDelegate _next;
    private readonly BffOptions _options;
    private readonly ILogger<BffMiddleware> _logger;

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="next"></param>
    /// <param name="options"></param>
    /// <param name="logger"></param>
    public BffMiddleware(RequestDelegate next, IOptions<BffOptions> options, ILogger<BffMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Request processing
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public async Task Invoke(HttpContext context)
    {
        // add marker so we can determine if middleware has run later in the pipeline
        context.Items[Constants.BffMiddlewareMarker] = true;

        // inbound: add CSRF check for local APIs 

        var endpoint = context.GetEndpoint();
        if (endpoint == null)
        {
            await _next(context);
            return;
        }

        var isBffEndpoint = endpoint.Metadata.GetMetadata<IBffApiEndpoint>() != null;
        if (isBffEndpoint)
        {
            var requireAntiForgeryCheck = endpoint.Metadata.GetMetadata<IBffApiSkipAntiforgry>() == null;
            if (requireAntiForgeryCheck)
            {
                if (!context.CheckAntiForgeryHeader(_options))
                {
                    _logger.AntiForgeryValidationFailed(context.Request.Path);

                    context.Response.StatusCode = 401;
                    return;
                }
            }
        }

        context.Response.OnStarting(() =>
        {
            // outbound: we assume that an API will never return a 302
            // if a 302 is returned, that must be the challenge to the OIDC provider
            // we convert this to a 401
            if (isBffEndpoint)
            {
                if (context.Response.StatusCode == 302)
                {
                    var requireResponseHandling = endpoint.Metadata.GetMetadata<IBffApiSkipResponseHandling>() == null;
                    if (requireResponseHandling)
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Headers.Remove("Location");
                        context.Response.Headers.Remove("Set-Cookie");
                    }
                }
            }

            return Task.CompletedTask;
        });

        await _next(context);
    }
}