# BeatMarathon (RealtimeRankMarathon)

Beat Saber 向け リアルタイム・ライバル順位可視化MODです。  
BeatLeader および ScoreSaber のランキングデータを事前取得し、譜面プレイ中の現在精度（Accuracy）に応じて、ライバルプレイヤーたちとの順位変動や抜きつ抜かれつの攻防を「マラソン大会のロードサイン（看板）」または「バー表示」形式でVR空間内にリアルタイム表示します。

GC（ガベージコレクション）によるカクつき・スパイクを極力排除した軽量・高パフォーマンス設計です。

---

## 🏃 主な特徴

1. **リアルタイム・ライバル追従**
   - BeatLeader / ScoreSaber のランキング（Global / Country / Friends）に対応。
   - プレイヤーのアバターアイコンと名前、アキュラシー（精度）を表示。
   - プレイ中の現在精度に応じて、前後のライバルを追い抜くアニメーション演出！
2. **2種類の表示スタイル**
   - **ロードサイン・マラソン方式 (RoadSignMarathon)**: コース脇に看板（キロポスト）が並ぶようなマラソン大会の臨場感。
   - **バー方式 (VerticalBar)**: 縦型バーで直感的に順位関係や前後のプレイヤー差を把握。
3. **ゲーム内Mod設定対応**
   - データソース切替（BeatLeader / ScoreSaber）
   - ランキング範囲（Global / Friends / Country）
   - 表示モードや位置の微調整

---

## 🔧 前提MOD
- [BSIPA](https://github.com/beat-saber-modding-group/BeatSaber-IPA-Reloaded) (4.3.0+)
- [SiraUtil](https://github.com/Auros/SiraUtil) (3.1.0+)
- [BeatSaberMarkupLanguage](https://github.com/monkeymanboy/BeatSaberMarkupLanguage) (BSML 1.12.0+)
- [SongCore](https://github.com/Kylemc1413/SongCore) (3.14.0+)

---

## 📦 インストール方法
1. 最新リリースの `BeatRankMarathon_v1.0.0.zip` をダウンロードします。
2. 解凍した `Plugins` フォルダ内の `RealtimeRankMarathon.dll` を、Beat Saber のインストール先にある `Plugins` フォルダへ配置してください。
