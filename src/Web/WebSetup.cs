using PairwiseGsb.Core;
using PairwiseGsb.Core.Storage;

namespace PairwiseGsb.Web;

public static class WebSetup
{
    public static void ConfigureServices(IServiceCollection services, string dataPath, bool includeBackgroundRunner = true)
    {
        services.AddSingleton(new JsonDocumentStore(Path.GetFullPath(dataPath)));
        services.AddSingleton<ComparisonService>();
        services.AddSingleton<BatchService>();
        if (includeBackgroundRunner)
        {
            services.AddHostedService<BackgroundBatchRunner>();
        }
    }
}
