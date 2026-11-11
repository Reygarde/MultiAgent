using AIPlatform.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIPlatform.Tests;

public sealed class DesktopSessionTests
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void DesktopRequiresALauncherGeneratedToken(string? token) =>
        Assert.Throws<InvalidOperationException>(() => new DesktopSession(true, token));

    [Theory]
    [InlineData(null, null, 401)]
    [InlineData("Bearer wrong", null, 401)]
    [InlineData("Bearer aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "https://evil.example", 403)]
    [InlineData("Bearer aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "http://tauri.localhost.evil.example", 403)]
    [InlineData("Bearer aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "null", 403)]
    [InlineData("Bearer aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, 200)]
    [InlineData("Bearer aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "http://tauri.localhost", 200)]
    [InlineData("Bearer aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "http://127.0.0.1:5173", 200)]
    public async Task MiddlewareRequiresTokenAndExactTrustedOrigin(string? authorization, string? origin, int expected)
    {
        using var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        if (origin is not null) context.Request.Headers.Origin = origin;
        var reached = false;
        await new DesktopSession(true, Token).InvokeAsync(context, _ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.Equal(expected == 200, reached);
        if (expected == 401) Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public void TokenIsSpecificToOneSessionAndDisabledSessionCannotAuthorize()
    {
        Assert.False(new DesktopSession(true, Token).Authorize("Bearer " + new string('b', 64)));
        Assert.False(new DesktopSession(false, null).Authorize(""));
    }
}
