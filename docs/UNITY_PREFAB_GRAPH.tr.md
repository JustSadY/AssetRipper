# Prefab variant/nested grafiği: etkin veri ve kaynak bağlantıları

Bu P13 dilimi kaynak sahibi olduğumuz **Unity 6000.4.0f1 / Linux64** temel prefab, variant ve nested prefab örneğini gerçek bundle → processing → export → Editor hattından geçirir. **Etkin değerler korunuyor; özgün variant/nested bağlantıları bu corpus'ta geri oluşmuyor.** Checker bu iki sonucu ayrı raporlar; sağlam değerler tam prefab rekonstrüksiyonu sayılmaz.

## Üretim raporundaki yeni bilgi

İki export yolunun `collection-export-report.json` dosyasında `PrefabReconstructions` listesi bulunur. SystemTester da aynı raporu vaka JSON'una gömer. Her satır:

- Root adı, collection/PathID ve prefab nesnesinin collection/PathID bilgisini;
- `PrefabOrigin`: `SourceSerializedObject`, `GeneratedFromHierarchy` veya `Unknown`;
- Bağımsız `AuthoringRelationsValidation=NotRun` sonucunu taşır.

Üretilmiş kökeni, `PrefabProcessor` yeni kök prefab nesnesini oluşturduğu anda `PrefabHierarchyObject.PrefabWasGenerated` ile işaretler. **Processed collection'da bulunmak tek başına üretim kanıtı değildir**; kaynaktan dönüştürülmüş veya özel bir yoldan gelmiş nesne için kanıt yoksa `Unknown` kalır. Serialized collection'daki mevcut prefab nesnesi de bağlantı doğrulamasından geçmiş sayılmaz. Mevcut üç parametreli constructor ve `Create` imzaları korunur.

Kimlikler iş içinde geçerlidir. Liste processing sonucu bulunan hiyerarşileri tanımlar; satır bulunması o prefab'ın başarıyla yazıldığı anlamına gelmez. `Succeeded` export collection döngüsünün mevcut anlamını korur; authoring roundtrip başarısı değildir. Bu dilim bağlantı tahmini yapmaz veya kaynak prefab GUID'sini uydurmaz.

## Gerçek Editor corpus'u

`PrefabGraphFixtureBuilder.Build` yeni geçici projede üç asset oluşturur:

| Prefab | Kaynak authoring yapısı | Kontrol edilen farklar |
|---|---|---|
| BaseRig | Regular | Socket, inherited/private alanlar, yerel ve harici referanslar |
| VariantRig | BaseRig'den Variant | Score 11 → 29, Unicode label, private alan, transform, ek inactive child ve değişmiş hedef |
| NestedRig | İçinde VariantRig instance'ı | İç bileşende score 41, farklı hedef, parent/child karşılıklı script referansı; container'da base/variant asset referansları |

Toplam **9 GameObject, 4 MonoBehaviour, 25 PPtr alanı** vardır. Null, dizi, GameObject/Transform/script, TextAsset ve prefab'lar arası gerçek asset referansları kapsanır. Ortak runtime sınıfı mevcut `ScenePayload`'dır.

Variant/nested yapı Unity'nin [SaveAsPrefabAsset sözleşmesiyle](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/PrefabUtility.SaveAsPrefabAsset.html) oluşturulur. Değişiklikler [RecordPrefabInstancePropertyModifications](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/PrefabUtility.RecordPrefabInstancePropertyModifications.html) ve SerializedObject ile kaydedilir. Asset'lar yeniden import edilir; bilinen sabitler ve gerçek nesne eşitlikleri kontrol edilir. Oracle, etkin alanlara ek olarak `GetPrefabAssetType`, karşılık gelen source root ve property override yollarını kaydeder. Variant'ın BaseRig'e, nested instance'ın VariantRig'e bağlı olduğu ve `score` / `peer` override'ları gerçekten doğrulanır.

Üç prefab aynı payload bundle'ına, TextAsset ayrı external bundle'a derlenir. Varsayılan ve `DisableWriteTypeTree` modları vardır. Bu prefab corpus'unda TypeTree'siz mod gerçekten assembly şemasına gider; dört bileşenin ilk `UnloadedStructure` durumu alanlar okunmadan sınanır. Gerçek DLL ve 20 pinli Unity metadata referansı kullanılır; DLL test host'unda yürütülmez.

**27 dosyalık** hash manifesti DLL, oracle, bundle/manifest dosyaları, yedi builder/runtime C# kaynağı, üç özgün `.prefab` ve üç `.meta` dosyasını içerir. Import'a yalnız bundle yolları verilir; özgün prefab/YAML dosyaları kurtarma girdisi olarak kullanılmaz. Kaynak DLL çıkarılan projenin Assets dizinine konmaz; Editor decompile edilmiş C# kodunu derler.

## Ölçülen sınır: etkin veri geçer, authoring grafiği kısmi kalır

Kaynak Editor oracle'ında bir Variant ve nested instance/source/override bilgisi bulunur. Bu corpus'un iki runtime bundle'ında **0 PrefabInstance nesnesi** vardır. Processing üç yeni kök prefab üretir; iki exporter bu üç kökü `GeneratedFromHierarchy` / `NotRun` olarak raporlar.

Çıkan proje Editor'da açıldığında üç asset **Regular** olarak yüklenir. Etkin override değerleri, inactive child, script alanları ve 25 referans hedefi korunur. Fakat source bağlantıları ve override listeleri aynı değildir. Sağlam checker sonucu bu nedenle:

```json
{
  "EffectiveDataPassed": true,
  "AuthoringRelationsMatched": false,
  "FullRoundTripStatus": "Partial"
}
```

Exit `0`, bu checker'da yalnız etkin veri kontrollerinin geçmesi demektir. Tam authoring sonucu ayrıca okunmalıdır. Bu davranış tek pinli corpus'tan ölçülmüştür; bütün bundle'larda aynı metadata'nın bulunmadığı iddia edilmez. Kaynak ilişkiler yokken yalnız ad/değer benzerliğinden variant veya nested bağlantısı kurulmaz.

## Test kapıları

`TestCategory=UnityPrefab` **16 vaka** içerir:

| Katman | Vaka | Beklenti |
|---|---:|---|
| Kurulumsuz rapor regresyonu | 4 | Source/generated/unknown ayrımı, JSON serialization ve boş bundle; hiçbirinde authoring başarı iddiası yok |
| Gerçek bundle okuma + processing | 4 | İki şema × external var/yok; 9 nesne, 4 bileşen, 25 alan/hedef; eksik external native ID'yi değiştirmez |
| Primary-content export | 2 | İki şemada başarılı export ve üç üretilmiş prefab köken satırı |
| Sağlam Unity-project → Editor | 2 | İki şemada etkin veri geçer, tam sonuç Partial kalır |
| Bozulmuş Unity-project → Editor | 4 | Script GUID, variant score, base prefab asset GUID ve nested child adı doğru hata kategorisinde reddedilir |

Cross-prefab negatif kontrolünde BaseRig `.meta` GUID'si değişir; NestedRig'in `links[0]` hedefi kopar ve `ReferenceMismatch` oluşur. Diğer negatifler `MissingScript`, `ScalarMismatch` ve `HierarchyMismatch` üretir. Compiler hatası, eksik Editor raporu veya `ValidationException` doğru negatif sonuç sayılmaz. Bozulmamış dosya, mutation hash'leri ve import öncesi snapshot korunur.

## Üretim, çalıştırma ve kanıt

```bash
python TestProjects/build_payload_fixture.py \
  --unity /path/to/6000.4.0f1/Editor/Unity \
  --prefabs --output /absolute/new-or-empty/prefab-corpus

ASSETRIPPER_UNITY_EDITOR_DATA=/path/to/6000.4.0f1/Editor/Data \
ASSETRIPPER_UNITY_EDITOR_EXECUTABLE=/path/to/6000.4.0f1/Editor/Unity \
ASSETRIPPER_UNITY_PREFAB_FIXTURE=/absolute/prefab-corpus \
dotnet test Source/AssetRipper.Tests --no-restore \
  -p:AllowMissingPrunePackageData=true \
  --filter TestCategory=UnityPrefab --logger trx
```

Mevcut kaynak proje veya dolu çıktı temizlenmez; geçici bir Editor projesi kullanılır. Corpus/executable yoksa **4 vaka çalışır, 12 vaka NotExecuted** olur. Yalnız executable yoksa corpus ile **10 vaka** çalışır: dört rapor + dört reader + iki primary; altı Editor vakası NotExecuted olur. Yapılandırılmış eksik/değişmiş corpus veya yanlış pinli referans skip sayılmaz.

`TestResults/unity-prefab/<schema>-<kind>-<id>/` altında oracle, manifest, `Evidence/Input/`, reader JSON'u, production collection raporu, `roundtrip-inputs.json`, `Evidence/BeforeImport/`, mutation'ın özgün dosyası ve Editor JSON sonuçları bulunur. CI yalnız seçilmiş JSON/Evidence yollarını toplar; Library cache'leri veya tam Editor log'ları yüklenmez. Unity kurulumu ve genel oyun kodu çalıştırma özelliği eklenmez.

## Yerel doğrulama

| Kontrol | Sonuç |
|---|---|
| Tüm solution, son Debug | **795/795 geçti**, sekiz proje; gerçek corpus ve Editor kapıları etkin |
| Yeni prefab kategorisi | **16/16 geçti**, Debug ve Release |
| İlgili Release ilk koşu | Core **258 geçti / 1 başarısız**; IO 3 ve GUI 4 geçti: toplam **265 geçti / 1 başarısız** |
| Ayrı scalar NUnit yeniden kontrolü | **1/1 geçti**, değişmeyen derlenmiş kodla |
| Kurulum değişkenleri olmadan | **4 geçti / 12 NotExecuted** |
| Yalnız Editor executable yokken | **10 geçti / 6 NotExecuted** |
| Son solution Release build | **0 hata / 1 mevcut Animator TODO uyarısı** |
| Bağımsız kanıt denetimi | 24 gerçek rapor grubu; **999 hash**, **100 YAML pointer GUID/fileID bağı** |

Bağımsız Python okuyucusu UnityFS bloklarını ve format-22 type/object tablolarını çözdü: iki payload'da 9 GameObject, 9 Transform, 4 MonoBehaviour ve 1 MonoScript; PrefabInstance yok. TypeTree açık/kapalı ayrımı doğrudan dosyada doğrulandı. 27 özgün corpus hash'i değişmedi. 648 girdi kopyası, 288 import öncesi snapshot dosyası, 16 mutation hash'i ve 20 Unity referans hash'i eşleşti. Sağlam Editor çıktılarında toplam 100 referans alanının YAML GUID/fileID bağları ayrıca kontrol edildi; source/variant/nested oracle bilgisi etkin veri kontrollerinden ayrı karşılaştırıldı.

Release'deki tek ilk başarısızlık, mevcut `EditorRoundTrip_AssemblySchema` scalar fixture'ında checker başlamadan Unity'nin Mono named-pipe `ToFileDescriptor` assert'iyle exit **134** vermesiydi; Editor raporu yazılmadı. Bu sonuç başarısız olarak korundu. Aynı çıkarılmış proje ayrı CLI kontrolünde, mevcut **12 C#/prefab/meta dosyasının hash'leri değiştirilmeden** yeniden açıldı; `Passed=true` ve exit **0** verdi. İlk `editor-run.json` ve TRX ezilmedi; ayrı `editor-recheck-report.json` / `editor-recheck-run.json` yazıldı. Ardından aynı NUnit vakası değişmeyen derlenmiş kodla ayrı koşuda **1/1 geçti**. Harness'a otomatik retry eklenmedi. Yeni prefab vakalarının iki sağlam sonucu Partial, dört negatif sonucu beklenen kategorilerde Failed idi.

Tüm Debug seti, mevcut constructor/Create imzaları korunan son API düzenlemesinden sonra tekrar geçti. Tam Release suite'in bilinen Debug.Assert bağımlı testi ilgili seçime dahil edilmedi; uzak CI çalıştırılmadı. Sonuç dizinleri `/tmp/assetripper-prefab-final-debug-results/`, `/tmp/assetripper-prefab-release-results/`, `/tmp/assetripper-prefab-release-io-results/`, `/tmp/assetripper-prefab-release-gui-results/`, `/tmp/assetripper-prefab-release-recheck-results/`, `/tmp/assetripper-prefab-unconfigured-results/` ve `/tmp/assetripper-prefab-no-editor-results/` altındadır. Bu geçici yollar kalıcı teslim değildir; seçilmiş test artifact kanıtları yukarıdaki yapıda korunur.

## Açık kalan işler

Gerçek authoring metadata'sını taşıyan eski/özel prefab formatlarında source/nested/override rekonstrüksiyonu, prefab instance içeren sahne, removed/added component override'ları, çoklu/additive sahne, başka Unity sürümleri/platformları ve Play Mode açıktır. Bu dilim shader, ECS veya IL2CPP kurtarmasını genişletmez. Sonraki bağımsız iş gerçek stripped scene/player corpus'u ve P10 IL2CPP provider yetenek ölçümüdür; bütün geliştirme planı bitmiş değildir.
