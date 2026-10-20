# Sahne bundle'ının dışa aktarımı ve Editor doğrulaması

Bu P13 dilimi, kaynak sahibi olduğumuz **Unity 6000.4.0f1 / StandaloneLinux64** sahnesini bundle'dan okuyup normal `ExportHandler.Process/Export` hattıyla Unity projesine çıkarır. Bağımsız checker, çıkan `.unity` dosyasını aynı Editor'da açıp hiyerarşi, script, alan ve nesne referanslarını karşılaştırır. Kaynak DLL, üretilen projenin Assets dizinine verilmez; Editor decompile edilmiş C# dosyalarını derler.

## Sahne adı düzeltmesi

Gerçek fixture, sahneyi `BuildPlayer-SceneFixture` adlı serialized entry'de taşır. Önceden collection adı dependency eşlemesi için normalize edilirken özgün yazım kayboluyor; scene path metadata'sı bulunmadığında dışa aktarılan sahne `buildplayer-scenefixture` adını alıyordu. İlk altı Editor vakası bu yanlış ad nedeniyle başarısız oldu.

`SerializedAssetCollection`, kaynak serialized dosyasının adını artık ayrı `OriginalName` alanında saklar. `Name` ve dependency çözümlemesi aynı normalize kimliği kullanır. `SceneDefinitionProcessor`, daha yüksek öncelikli scene path bilgisi yoksa, yalnız `SerializedBundle` içindeki tam `BuildPlayer-` önekini tanır ve sonrasındaki özgün sahne adını kullanır. Boş, `.` / `..` veya yol ayırıcı/NUL içeren suffix için bu çıkarımı yapmaz. Generated collection'lara kaynak adı uydurulmaz.

Unity'nin [UnityDataTools format notları](https://github.com/Unity-Technologies/UnityDataTools/blob/main/Documentation/assetbundle-format.md#serializedfile-names-inside-a-bundle), bu adlandırmayı `BuildPipeline.BuildAssetBundles` için açıklar; SBP/Addressables farklı CAB adları kullanabilir. Düzeltme CAB hash'lerinden sahne adı üretmez. Eksik `Assets/...` dizin yolu da geri oluşturulmuş sayılmaz: bu fixture `Assets/Scenes/SceneFixture.unity` fallback yoluna çıkar. Mevcut BuildSettings / AssetBundle scene path bilgisi önceliğini korur.

## Kaynak corpus ve bağımsız oracle

`SceneFixtureBuilder.Build`, yeni geçici projede boş bir sahne oluşturur, kaydeder ve yeniden açar. Kaynak oracle, `SerializedObject` ile okunur; ayrıca elle belirtilmiş sabit alanlar ve gerçek nesne eşitlikleriyle doğrulanır:

- İki root dahil beş GameObject; üç seviyeli parent/child hiyerarşisi, sibling sırası ve inactive child.
- Yerel konum, quaternion dönüş ve ölçek değerleri.
- İki `ScenePayload` bileşeni; inherited/private SerializeField, sayı, Unicode metin ve nested alanlar.
- Toplam **13 PPtr alanı**: GameObject, Transform, karşılıklı iki MonoBehaviour, dizi, null ve ayrı bundle'daki TextAsset.

Scene ve external asset ayrı build map'lere gider. Aynı scene map'i varsayılan seçeneklerle ve `DisableWriteTypeTree` eklenerek iki kez derlenir. DLL, oracle, kaynak `.unity`, bütün runtime/builder C# dosyaları ve bundle/manifest girdileri SHA-256 manifestine alınır. Bu corpus'ta **21 dosya** hash'lenir; manifest kendisini hash'lemez. Kaynak asset GUID/local ID ile bundle FileID/PathID eşit varsayılmaz.

## Build seçeneği ile kullanılan şema ayrı kaydedilir

Bu fixture'da sahnenin iki MonoBehaviour tipinde **iki build modunda da 36 TypeTree düğümü** bulundu. İlk reader testlerindeki `DisableWriteTypeTree → UnloadedStructure` varsayımı bu nedenle reddedildi. Bu gözlem yalnız mevcut pinli corpus'a aittir; bütün Unity sahnelerinin veya sürümlerinin davranışı olarak genellenmez. [Unity API belgesi](https://docs.unity3d.com/6000.2/Documentation/ScriptReference/BuildAssetBundleOptions.DisableWriteTypeTree.html) build seçeneğinin amacını açıklar; burada gerçekleşen şema yolu doğrudan factory girişinde ölçülür.

Üç senaryo ayrı raporlanır:

| Senaryo | Kaynak build | Parser'a verilen MonoBehaviour metadata'sı | Beklenen okuma |
|---|---|---|---|
| `TypeTree` | Varsayılan | Özgün TypeTree | TypeTree |
| `DisableWriteTypeTree` | Flag açık | Özgün TypeTree, corpus'ta hâlâ mevcut | TypeTree |
| `AssemblyFallback` | Flag açık | Test factory'si yalnız bu sınıf için type metadata'sını vermez | Assembly / deferred structure |

Son satır **kontrollü fallback deneyidir**; gerçekten TypeTree'siz üretilmiş sahne bundle'ı diye sunulmaz. Kaynak dosyalar ve nesne baytları değişmez. Assembly yolu gerçek Editor-compiled DLL ve önceden pinlenmiş 20 Unity referans image'ını metadata olarak kullanır; fixture DLL'si test host'unda yürütülmez. Diğer nesne tiplerinin metadata'sına müdahale edilmez.

Raporlar `BuildMode`, `SchemaSource`, `ControlledTypeTreeSuppression` ve her MonoBehaviour için kaynak adı, PathID, özgün TypeTree düğüm sayısı, ilk structure türü ve nesne SHA-256'sını taşır. Böylece başarı build bayrağından çıkarılmaz.

## Test sözleşmesi

`TestCategory=UnityScene` toplam **21 vaka** içerir:

| Katman | Vaka | Kontrol |
|---|---:|---|
| Her zaman çalışan scene naming regresyonu | 8 | Özgün case, normalize dependency kimliği, explicit path önceliği, generated collection ve dar önek kuralı |
| Gerçek bundle okuması | 6 | Üç şema senaryosu × external bundle var/yok; beş nesne, iki bileşen, alanlar ve 13 pointer |
| Sağlam export → Editor | 3 | Üç senaryoda gerçek dışa aktarılmış sahne, derlenen C#, hiyerarşi ve referans kimlikleri |
| Bozulmuş export → Editor | 4 | MissingScript, ScalarMismatch, ReferenceMismatch ve HierarchyMismatch |

External bundle yokken native pointer ID'leri tam yüklenen örnekle birebir aynı kalmalıdır; çözülemeyen hedef null olsa bile serialized ID sıfırlanmaz. Bu vakalar reader kontrolüdür, eksik bundle ile başarılı Editor export'u iddiası değildir.

Editor checker, `EditorSceneManager.OpenScene` ile çıkarılan sahneyi açar. Root sibling sırası dahil hiyerarşiyi, transform'ları, inactive nesneyi, iki bileşenin namespace/type/assembly'sini, missing-script slot'larını, private/inherited alanları ve MonoScript `11500000` local ID bağını denetler. Yerel referansları gerçek nesne eşitliğiyle, TextAsset'ı tür/yol/GUID/içerikle kontrol eder. Yalnız Editor exit kodu başarı için yeterli değildir.

Dört negatif vaka yalnız üretilen projenin bir girdisini değiştirir: script `.meta` GUID'si, `score: 17`, external TextAsset `.meta` GUID'si veya inactive child adı. Her biri kendi hata kodu, gerçek JSON raporu ve exit `1` üretmelidir; `ValidationException`, compiler veya başlatma hatası geçerli negatif sonuç sayılmaz. Özgün dosya `Evidence/Mutation/original-file.bin` altında, değişmiş hali import öncesi snapshot'ta saklanır; iki SHA-256 mutation kaydına yazılır.

## Çalıştırma

```bash
python TestProjects/build_payload_fixture.py \
  --unity /path/to/6000.4.0f1/Editor/Unity \
  --scenes --output /absolute/new-or-empty/scene-corpus

ASSETRIPPER_UNITY_EDITOR_DATA=/path/to/6000.4.0f1/Editor/Data \
ASSETRIPPER_UNITY_EDITOR_EXECUTABLE=/path/to/6000.4.0f1/Editor/Unity \
ASSETRIPPER_UNITY_SCENE_FIXTURE=/absolute/scene-corpus \
dotnet test Source/AssetRipper.Tests --no-restore \
  -p:AllowMissingPrunePackageData=true \
  --filter TestCategory=UnityScene --logger trx
```

Runner mevcut projeyi veya dolu çıktı dizinini temizlemez. Yeni geçici proje kullanır; kullanılabilir Editor lisansı ve Linux modülü önceden kurulu olmalıdır. Scene corpus yoksa altı reader kontrolü, executable yoksa yedi Editor kontrolü açıkça `Skipped/NotRun` olur; sekiz naming kontrolü çalışır. Yapılandırılmış eksik dosya, yanlış manifest hash'i veya eksik pinli referans skip sayılmaz. Editor rapor yazmadan çökerse test başarısızdır; otomatik retry yoktur.

## Kanıt dizinleri

`TestResults/unity-scene/<scenario>-<kind>-<id>/` altında:

- Oracle, SHA manifesti ve `Evidence/Input/` altında değişmemiş 21 kaynak girdi;
- reader için `scene-bundle-verification.json`;
- Editor için `roundtrip-inputs.json`, `Evidence/BeforeImport/` snapshot'ı ve negatiflerde `Evidence/Mutation/`;
- `editor-run.json` içinde exit code ve seçilmiş compiler hataları; `editor-import-report.json` içinde gözlenen değerler ve hata kodları.

CI artifact listesine yalnız JSON ve `Evidence` yolları eklendi. Library cache'leri ve tam Editor log'ları yüklenmez. Standart CI'a Unity kurulumu veya otomatik oyun kodu yürütme eklenmedi. Bu, kaynak sahibi olduğumuz fixture'a ait isteğe bağlı kabul kapısıdır; SystemTester genel oyun kodunu çalıştırmaz.

## Yerel doğrulama

| Kontrol | Sonuç |
|---|---|
| Tüm solution, Debug | **779/779 geçti**, sekiz test projesi; gerçek corpus ve Editor kontrolleri etkin |
| İlgili Release seti | **250/250 geçti**: core 238 + output-directory 5 + IO 3 + GUI 4 |
| Yeni sahne kategorisi | **21/21 geçti**, Debug ve Release |
| Scene corpus / executable olmadan | **8 geçti, 13 NotExecuted**; tekil TRX sonuçları doğrulandı |
| Solution Release build | Başarılı, **0 hata / 12 mevcut uyarı**; DocExtraction converter/source-generation ve iki TODO |
| Bağımsız kanıt denetimi | Debug/Release toplam 26 rapor; **915** corpus/nesne/snapshot/mutation hash kontrolü ve ayrıca **20** pinli Unity referansı |

Bağımsız Python kontrolü UnityFS bloklarını ve format-22 serialized type/object tablolarını doğrudan okuyarak iki build'de de TypeTree bulunduğunu ve her rapordaki iki MonoBehaviour nesne SHA-256'sını doğruladı. Kaynak corpus'un 21 hash'i değişmedi. Üç senaryoda missing-external pointer ID'leri aynıydı. Üçer sağlam Editor raporundaki hiyerarşi/scalar değerleri, scene/meta GUID'si, iki MonoScript `11500000` bağı ve TextAsset GUID/içerikleri snapshot'larla eşleşti. Sekiz negatif snapshot yalnız amaçlanan alanı/GUID'yi değiştirdi; özgün/bozulmuş dosya hash'leri eşleşti.

İlk reader koşuları **10 geçti / 2 başarısız** sonucu verdi: başarısız iki vaka build bayrağını şema yokluğu sayıyordu. Factory girişindeki düğüm ölçümü ve bağımsız binary okuma bu varsayımı çürüttü; testler metadata'yı gerçekten kullandıkları yolu raporlayacak şekilde düzeltildi. Son tam Debug ve ilgili Release koşularında başarısız test veya native Editor retry olmadı. Tam Release suite'in önceden bilinen Debug.Assert bağımlı testi bu ilgili sete dahil değildir; uzak CI çalıştırılmadı.

Yerel sonuç dizinleri `/tmp/assetripper-scene-debug-results/`, `/tmp/assetripper-scene-release-results/`, `/tmp/assetripper-scene-release-directories-results/`, `/tmp/assetripper-scene-release-io-corrected-results/`, `/tmp/assetripper-scene-release-gui-corrected-results/` ve `/tmp/assetripper-scene-unconfigured-results/` altındadır. İlk boş IO/GUI filtreleri sıfır test seçti; doğru sınıf filtreleriyle çalıştırılan 3+4 vaka sayıya dahil edildi. Geçici dizinler kalıcı teslim değildir; corpus ve yukarıdaki test artifact kanıtları ayrıca korunur.

## Sınırlar ve sıradaki iş

Bu dilim tek Unity sürümü, Linux64 ve tek sahneyle sınırlıdır. Gerçek stripped scene/player, çoklu/additive sahne, prefab instance/variant bağlantıları, asmdef/üçüncü taraf assembly grafiği, eski Mono profilleri, Play Mode ve IL2CPP açık kalır. Sahnenin açılması davranış eşdeğerliği değildir; sahne GUID'sinin özgün kaynak GUID'si olduğu da iddia edilmez. Ana geliştirme planı tamamlanmış değildir.

Sonraki P13 dilimi prefab instance/variant ve override bağlarını gerçek Editor oracle'ıyla doğrulamak; gerçek stripped scene/player corpus'u için ayrı üretim kapısı kurmaktır.
