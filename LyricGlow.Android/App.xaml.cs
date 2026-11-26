using System;
using LyricGlow.Services;

namespace LyricGlow.Android;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
		{
			Log.Write("Android unhandled exception: " + args.ExceptionObject);
		};
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new NavigationPage(new MainPage()));
	}
}
