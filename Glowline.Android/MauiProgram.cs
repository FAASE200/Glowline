using System.IO;
using Glowline.Android.Services;
using Glowline.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using Microsoft.Maui.Storage;

namespace Glowline.Android;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				fonts.AddFont("Inter-Regular.ttf", "Inter");
				fonts.AddFont("Inter-Medium.ttf", "InterMedium");
				fonts.AddFont("Inter-SemiBold.ttf", "InterSemiBold");
				fonts.AddFont("Inter-Bold.ttf", "InterBold");
			})
			.ConfigureLifecycleEvents(events =>
			{
#if ANDROID
				events.AddAndroid(android => android
					.OnResume(_ => AppForeground.Set(true))
					.OnPause(_ => AppForeground.Set(false)));
#endif
			});

		StoragePaths.DataDirectory = FileSystem.AppDataDirectory;
		Directory.CreateDirectory(StoragePaths.CacheDirectory);
		var settings = new AndroidSettingsStore();
		var session = new AndroidSessionService(Platform.AppContext);
		var state = AppState.Create(settings, session);
		var updates = AndroidUpdateService.Create(settings);
		builder.Services.AddSingleton(settings);
		builder.Services.AddSingleton(session);
		builder.Services.AddSingleton(state);
		builder.Services.AddSingleton(updates);

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
