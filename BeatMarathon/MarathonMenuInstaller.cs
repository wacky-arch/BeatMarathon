using BeatMarathon.UI;
using Zenject;

namespace BeatMarathon;

internal class MarathonMenuInstaller : Installer
{
	public override void InstallBindings()
	{
		base.Container.BindInterfacesAndSelfTo<SettingsViewController>().AsSingle();
		base.Container.BindInterfacesAndSelfTo<MenuPreloadController>().AsSingle();
	}
}
