using BeatMarathon.Services;
using Zenject;

namespace BeatMarathon;

internal class MarathonAppInstaller : Installer
{
	public override void InstallBindings()
	{
		base.Container.BindInstance(PluginConfig.Instance).AsSingle();
		base.Container.BindInterfacesAndSelfTo<AvatarCacheService>().AsSingle();
		base.Container.BindInterfacesAndSelfTo<LeaderboardFetcher>().AsSingle();
	}
}
