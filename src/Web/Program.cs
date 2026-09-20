using PairwiseGsb.Core;
using PairwiseGsb.Web;

var builder = WebApplication.CreateBuilder(args);
var dataPath = builder.Configuration["PAIRWISE_GSB_DATA"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "pairwise-gsb.json");
WebSetup.ConfigureServices(builder.Services, dataPath);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapApi();
app.Run();

public partial class Program;
