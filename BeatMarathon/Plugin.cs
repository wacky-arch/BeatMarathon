using System;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using IPA.Logging;
using SiraUtil.Zenject;

namespace BeatMarathon;

[Plugin(RuntimeOptions.DynamicInit)]
[NoEnableDisable]
public class Plugin
{
	internal static Logger Log { get; private set; }

	[Init]
	public Plugin(Logger logger, Config conf, Zenjector zenjector)
	{
		Log = logger;
		PluginConfig.Instance = conf.Generated<PluginConfig>();
		zenjector.UseLogger(logger);
		zenjector.Install<MarathonAppInstaller>(Location.App, Array.Empty<object>());
		zenjector.Install<MarathonMenuInstaller>(Location.Menu, Array.Empty<object>());
		zenjector.Install<MarathonPlayerInstaller>(Location.Player, Array.Empty<object>());
	}
}
