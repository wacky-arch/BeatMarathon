using BeatMarathon.Managers;
using BeatMarathon.UI;
using Zenject;

namespace BeatMarathon;

internal class MarathonPlayerInstaller : Installer
{
	public override void InstallBindings()
	{
		base.Container.BindInterfacesAndSelfTo<RankPlatePool>().AsSingle();
		base.Container.BindInterfacesAndSelfTo<MarathonManager>().AsSingle();
	}
}
