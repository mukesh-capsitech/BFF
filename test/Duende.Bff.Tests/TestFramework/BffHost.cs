﻿using Duende.IdentityServer.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;

namespace Duende.Bff.Tests.TestFramework
{
    public record ClaimRecord(string type, string value);

    public class BffHost : GenericHost
    {
        private readonly IdentityServerHost _identityServerHost;
        private readonly ApiHost _apiHost;
        private readonly string _clientId;

        public BffHost(string baseAddress = "https://app") : base(baseAddress)
        {
            OnConfigureServices += ConfigureServices;
            OnConfigure += Configure;
        }

        public BffHost(IdentityServerHost identityServerHost, ApiHost apiHost, string clientId, string baseAddress = "https://app") 
            : this(baseAddress)
        {
            _identityServerHost = identityServerHost;
            _apiHost = apiHost;
            _clientId = clientId;
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();
            services.AddAuthorization();

            var bff = services.AddBff();

            if (_apiHost is object)
            {
                bff.ConfigureTokenClient()
                    .ConfigurePrimaryHttpMessageHandler(() => _apiHost.Server.CreateHandler());
            }

            services.AddAuthentication("cookie")
                .AddCookie("cookie");

            if (_identityServerHost is object)
            {
                bff.AddServerSideSessions();

                services.AddAuthentication(options =>
                {
                    options.DefaultChallengeScheme = "oidc";
                    options.DefaultSignOutScheme = "oidc";
                })
                    .AddOpenIdConnect("oidc", options =>
                    {
                        options.Authority = _identityServerHost.Url();

                        options.ClientId = _clientId;
                        options.ClientSecret = "secret";
                        options.ResponseType = "code";
                        options.ResponseMode = "query";

                        options.MapInboundClaims = false;
                        options.GetClaimsFromUserInfoEndpoint = true;
                        options.SaveTokens = true;

                        options.Scope.Clear();
                        var client = _identityServerHost.Clients.Single(x => x.ClientId == _clientId);
                        foreach (var scope in client.AllowedScopes)
                        {
                            options.Scope.Add(scope);
                        }
                        if (client.AllowOfflineAccess)
                        {
                            options.Scope.Add("offline_access");
                        }

                        options.BackchannelHttpHandler = _identityServerHost.Server.CreateHandler();
                    });
            }
        }

        private void Configure(IApplicationBuilder app)
        {
            app.UseAuthentication();
            
            app.UseRouting();

            app.UseBff();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/local", async context=> 
                {
                    var sub = context.User.FindFirst(("sub"));
                    if (sub == null) throw new Exception("sub is missing");

                    var response = new
                    {
                        path = context.Request.Path.Value,
                        sub = sub,
                        claims = context.User.Claims.Select(x => new { x.Type, x.Value }).ToArray()
                    };

                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(response)); 
                })
                    .RequireAuthorization()
                    .AsLocalBffApiEndpoint();

                endpoints.MapBffManagementEndpoints();

                if (_apiHost is object)
                {
                    endpoints.MapRemoteBffApiEndpoint("/api", _apiHost.Url())
                        .RequireAccessToken();
                }
            });
        }

        public async Task<bool> GetIsUserLoggedInAsync()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Url("/bff/user"));
            req.Headers.Add("x-csrf", "1");
            var response = await BrowserClient.SendAsync(req);

            (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Unauthorized).Should().BeTrue();

            return response.StatusCode == HttpStatusCode.OK;
        }

        public async Task<ClaimRecord[]> GetUserClaimsAsync()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Url("/bff/user"));
            req.Headers.Add("x-csrf", "1");

            var response = await BrowserClient.SendAsync(req);

            response.StatusCode.Should().Be(200);
            response.Content.Headers.ContentType.MediaType.Should().Be("application/json");

            var json = await response.Content.ReadAsStringAsync();
            var claims = JsonSerializer.Deserialize<ClaimRecord[]>(json);

            return claims;
        }

        
        public async Task BffLoginInAsync(string sub)
        {
            await _identityServerHost.IssueSessionCookieAsync(new Claim("sub", sub));
            var response = await BrowserClient.GetAsync(Url("/bff/login"));
            response.StatusCode.Should().Be(302); // authorize

            response = await _identityServerHost.BrowserClient.GetAsync(response.Headers.Location.ToString());
            response.StatusCode.Should().Be(302); // client callback

            response = await BrowserClient.GetAsync(response.Headers.Location.ToString());
            response.StatusCode.Should().Be(302); // root
            response.Headers.Location.ToString().Should().Be("/");

            (await GetIsUserLoggedInAsync()).Should().BeTrue();
        }

    }
}