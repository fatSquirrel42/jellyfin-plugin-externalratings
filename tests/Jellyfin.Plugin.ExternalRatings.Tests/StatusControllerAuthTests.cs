using System.Linq;
using System.Reflection;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class StatusControllerAuthTests
{
    [Fact]
    public void Controller_IsApiController()
    {
        typeof(StatusController).GetCustomAttribute<ApiControllerAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void Controller_RequiresElevation()
    {
        var authorize = typeof(StatusController).GetCustomAttribute<AuthorizeAttribute>();

        authorize.Should().NotBeNull();
        authorize!.Policy.Should().Be("RequiresElevation");
    }

    [Fact]
    public void Controller_HasNoAnonymousAccess()
    {
        typeof(StatusController).GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();

        var anonymousActions = typeof(StatusController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null);

        anonymousActions.Should().BeEmpty();
    }

    [Fact]
    public void Controller_IsRoutedUnderPlugins()
    {
        var route = typeof(StatusController).GetCustomAttribute<RouteAttribute>();

        route.Should().NotBeNull();
        route!.Template.Should().Be("Plugins/ExternalRatings");
    }

    [Theory]
    [InlineData(nameof(StatusController.Restore), "Restore")]
    [InlineData(nameof(StatusController.ClearCache), "ClearCache")]
    public void MutatingActions_ArePostWithExpectedRoute(string methodName, string template)
    {
        var post = typeof(StatusController).GetMethod(methodName)!.GetCustomAttribute<HttpPostAttribute>();

        post.Should().NotBeNull();
        post!.Template.Should().Be(template);
    }
}
