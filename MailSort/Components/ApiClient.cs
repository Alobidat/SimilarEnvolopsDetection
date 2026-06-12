using Microsoft.AspNetCore.Components;

namespace MailSort.Components;

/// <summary>
/// Builds absolute URIs to our own /api endpoints from a Blazor component.
/// Server-side Blazor HttpClient requires absolute URIs, but the app's port
/// is dynamic, so we can't hardcode one. NavigationManager.BaseUri is the
/// right source of truth for the current request's origin.
/// </summary>
public class ApiClient
{
    private readonly IHttpClientFactory _factory;
    private readonly NavigationManager _nav;

    public ApiClient(IHttpClientFactory factory, NavigationManager nav)
    {
        _factory = factory;
        _nav = nav;
    }

    public HttpClient Http => _factory.CreateClient();
    public string Api(string path) => new Uri(new Uri(_nav.BaseUri), path).ToString();
}
