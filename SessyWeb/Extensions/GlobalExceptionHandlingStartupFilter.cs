using SessyCommon.Extensions;

public class GlobalExceptionHandlingStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(context =>
                {
                    var exceptionHandlerPathFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
                    var logger = app.ApplicationServices.GetRequiredService<ILogger<GlobalExceptionHandlingStartupFilter>>();

                    if (exceptionHandlerPathFeature?.Error != null)
                    {
                        Console.WriteLine($"An unexpected error occurred:\n\n{exceptionHandlerPathFeature.Error.ToDetailedString()}");
                    }

                    context.Response.Redirect("/error");

                    return Task.CompletedTask;
                });
            });

            next(app);
        };
    }
}
