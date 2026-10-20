# P09 — Gerçek Unity Editor çıktısıyla alan ve referans doğrulaması

Bu ikinci dilimde kaynak sahibi olduğumuz küçük bir proje **Unity 6000.4.0f1 Editor'da gerçekten açıldı, derlendi ve AssetBundle olarak üretildi**. P09'un önceki elle yazılmış payload oracle'ına ek olarak, Unity'nin serialize ettiği gerçek alanlar ve PPtr hedefleri iki okuma yolunda doğrulandı. Bu dilim test/corpus altyapısı ekler; üretim parser'ına veya paket sürümlerine yeni değişiklik gerektirmedi.

## Üretim ve bağımsız oracle

[TestProjects/MonoBehaviourPayload](../TestProjects/MonoBehaviourPayload) sıfır package bağımlılığı olan kaynak projedir. Unity MCP bağlantısı bu oturumda mevcut olmadığından `unity-mcp-orchestrator` rehberinin derleme/hata kontrolü yaklaşımı batch-mode CLI ile uygulandı. Editor ve build log'ları yerel geçici projede tutuldu; lisans veya mevcut kullanıcı projesi değiştirilmedi.

`PayloadFixtureBuilder.Build` şu adımları yapar:

1. Bir `EditorPayload` bileşeni, `LocalTarget` child GameObject ve ayrı `ExternalTarget` TextAsset oluşturur. Kalıtılan/private SerializeField alanları, UTF-8 string, int dizisi, scalar/dizi/nested referanslar ve null pointer bulunur.
2. Prefab ve sahneyi kaydeder. Sahneyi yeniden açar; `SerializedObject` ile gözlenen bütün seçili değerleri ve hedeflerin null/name/type/text özelliklerini prefab'ın kaydedilmiş haliyle karşılaştırır. Ancak bu kontrol geçerse `EditorSceneReadbackVerified=true` yazar.
3. `SerializedObject` gözlemlerini `oracle.json` içine kaydeder. Beklenen değerler testte ayrıca sabit oracle ile kontrol edilir: `-17`, `-1234`, `true`, `"Merhaba ☃"`, `[7,-9,42]`, nested `91`.
4. Linux64 için `payload` ve `external` adlı iki bundle üretir. Aynı build map hem normal TypeTree ile hem `DisableWriteTypeTree` seçeneğiyle ayrı dizinlere yazılır. Üretim API'si [BuildPipeline.BuildAssetBundles](https://docs.unity3d.com/6000.4/Documentation/ScriptReference/BuildPipeline.BuildAssetBundles.html), seçenek [DisableWriteTypeTree](https://docs.unity3d.com/6000.4/Documentation/ScriptReference/BuildAssetBundleOptions.DisableWriteTypeTree.html) belgelerinde açıklanır.
5. Editor'ın derlediği `Assembly-CSharp.dll`, fixture/builder kaynakları, sahne ve prefab kanıtlarını kopyalar. Bu ilk corpus **18** girdi dosyasının SHA-256 manifestini içerir. Managed-reference örneği eklendikten sonraki yeni üretimler şablondaki bütün C# kaynaklarını saklar ve **20** dosya içerir; ilk corpus geçerli kalır. Unity'nin DLL'leri depoya veya bu fixture paketine kopyalanmaz.

Kaynak `.unity` sahnesi Editor'da yeniden açılıp kontrol edilir; bundle'ın içerdiği test nesnesi prefab'tır. Bu, serialize edilmiş sahne bundle'ının tamamının AssetRipper ile doğrulanması veya çıkarılan projenin Editor'a geri import'u değildir. Fixture kaynağında Editor/Player'a göre alan düzenini değiştiren koşullu alan bulunmaz. `Assembly-CSharp.dll`, Editor derlemesidir; eski Mono compiler veya standalone player build'i olarak tanıtılmaz.

## PPtr kimliği ve doğrulama sınırı

Unity oracle'ı hedefin adını, gerçek Unity tipini, null durumunu ve TextAsset içeriğini okur. Kaynak GUID/local-file-ID de [TryGetGUIDAndLocalFileIdentifier](https://docs.unity3d.com/6000.4/Documentation/ScriptReference/AssetDatabase.TryGetGUIDAndLocalFileIdentifier.html) ile kanıta eklenir.

**Kaynak asset local ID'si bundle PathID'si değildir.** Test bu iki ID'yi eşit saymaz. AssetRipper'ın okuduğu `(FileID, PathID)` kendi koleksiyon/dependency grafiğinde çözümlenir; bulunan nesnenin adı, ClassID'si, yerel/harici koleksiyonda oluşu ve varsa metin içeriği Unity oracle'ıyla eşleşir. `direct` ile `links[0]` yerel GameObject'e; `links[1]` ve `nested.target` harici TextAsset'e gider; `links[2]` null kalır.

Test matrisi:

| TypeTree | Harici bundle | Kontrol |
|---|---|---|
| Var | Verildi | TypeTree'den hemen okunan alanlar ve beş referans yolu/hedefi |
| Yok | Verildi | Önce `UnloadedStructure`, sonra gerçek DLL metadata'sından şema ve aynı hedefler |
| Var | Verilmedi | Alanlar korunur; harici hedefler çözümlenmez, null pointer ile karıştırılmaz |
| Yok | Verilmedi | Assembly şemasıyla aynı eksik-dependency davranışı |

Harici bundle verilmediğinde pointer ID'leri, aynı bundle'ın dependency dahil yüklenmiş haliyle ayrıca karşılaştırılır; eksik hedefler için ID'ler değiştirilmez. Beş referansın sırası ve alan yolları da doğrulanır. Kaynak GUID/fileID → export GUID/fileID dönüşümü P13'ün ayrı kabul kapısıdır.

## Tekrarlanabilir kullanım

```sh
python TestProjects/build_payload_fixture.py \
  --unity /path/to/6000.4.0f1/Editor/Unity \
  --output /path/to/new-or-empty/fixture

ASSETRIPPER_UNITY_EDITOR_DATA=/path/to/6000.4.0f1/Editor/Data \
ASSETRIPPER_UNITY_PAYLOAD_FIXTURE=/path/to/fixture \
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter TestCategory=UnityEditorPayload --logger trx
```

Python runner şablonu yeni geçici projeye kopyalar, Editor'ı orada çalıştırır, exit code/manifest/hash'leri kontrol eder. Var olan dolu çıktı dizinini temizlemez. Kurulu ve çalışır Editor lisansı ile Linux build desteği gerekir. Build log'u konumu çıktıda gösterilir. GUID/local ID ve bundle hash'leri yeni üretimde değişebilir; semantik oracle sabittir. Corpus bir Unity sürümü/platformuyla sınırlıdır.

`ASSETRIPPER_UNITY_PAYLOAD_FIXTURE` yoksa dört gerçek girdi testi açıkça `Skipped/NotRun` olur; beş bağımsız manifest/loader kontrolü yine çalışır. Değişmiş/eksik dosya, göreli yol ihlali, sürüm/target veya oracle uyuşmazlığı hata verir. Yapılandırılmış fixture için pinli Editor referansları yoksa test atlanmaz, başarısız olur. DLL metadata çözümlemesi P07'nin 20 hash-pinned Unity/.NET Standard referans image'ını kullanır; host SDK/TPA fallback'i yapılmaz. Bu metadata okuması fixture DLL'sini yürütmez ve bu dilimde yeniden C# derleme kontrolü yapılmış sayılmaz.

## Kanıt dosyaları

Test çıktısındaki `TestResults/unity-editor-payload/<schema>-<external>-<uuid>/` dizini şunları içerir:

- Unity oracle'ı, girdi manifesti ve `editor-payload-verification.json`;
- `Input/` altında hash'i doğrulanmış 18 kaynak/bundle/DLL dosyası;
- Raporda altı gözlenen alan değeri, beş PPtr yolu ve gerçek pointer ID'leri, hedef koleksiyon/adı, çözüm sonucu ve referans profili.

JSON NUnit attachment olur; bütün dizin CI artifact listesine eklendi. Standart GitHub Actions ortamında Editor/corpus otomatik kurulmaz; dört testin atlanması gerçek Unity kontrolü başarılı demek değildir. Uzak CI burada tetiklenmedi. Tam Editor log'ları test artifact'ına eklenmez.

Yerel üretilmiş corpus git tarafından yok sayılan `Source/0Bins/TestCorpus/UnityPayload6000.4-20261020-b149bc36/` altında korunuyor. Bu yol yerel çalışma kanıtıdır; binary'ler commit edilmedi. Üretim script'iyle yeni bir corpus oluşturulabilir.

## Yerel doğrulama sonucu

| Kontrol | Sonuç |
|---|---|
| Yeni testler | **9/9 geçti**: 4 gerçek bundle matrisi + 5 bağımsız loader/manifest kontrolü |
| Unity üretimi | CLI exit `0`; sahne/prefab readback geçti, TypeTree'li/TypeTree'siz iki build map tamamlandı |
| Tüm solution, Debug | **707/707 geçti**, 8 test projesi; gerçek Unity referansları ve Editor corpus etkin |
| İlgili Release testleri | **178/178 geçti**, core 171 + IO 3 + GUI 4; aynı corpus etkin |
| Solution Release build | **0 uyarı / 0 hata** |
| Bağımsız kanıt denetimi | Corpus'un 18 hash'i ve repo C# kaynakları eşleşti; sekiz raporun 18'er input hash'i, altı alan değeri ve beş pointer sonucu doğrulandı |
| Eksik harici bundle | İki schema/runner kombinasyonunda pointer ID'leri tam yüklemeyle aynı kaldı |
| Fixture yapılandırılmayan kontrol | 5 loader testi geçti; 4 integration testi CLI'da `Skipped`, ayrı TRX result kayıtlarında `NotExecuted` |
| Çıkarılan projenin Editor import'u / IL2CPP | **NotRun** |

Build/test için bu ortamda `--no-restore -p:AllowMissingPrunePackageData=true` kullanıldı. SDK/paket sürümü değiştirilmedi. Tüm Release suite'in geçtiği iddia edilmiyor; mevcut Debug-assert test sınırı [baseline notlarında](DECOMPILATION_BASELINE.tr.md) kayıtlıdır. Fixture olmadan yapılan kontrolde TRX üst `notExecuted` sayacı sıfır görünse de dört ayrı test sonucu `NotExecuted` idi; atlanma kontrolü tek tek sonuçlardan doğrulandı.

## Açık kapsam

P09'un managed-reference/SerializeReference graph kimlikleri, başka Unity/Mono/platform kombinasyonları, serialize edilmiş sahne bundle'ları ve büyük veri/performance matrisi açık. Bu örnek IL2CPP kurtarması değildir. `ExportedProjectEditorImport=NotRun` açık tutulur: AssetRipper'ın dışa aktardığı projenin Unity Editor'de eksik script/ref olmadan açılması P13'te ayrıca doğrulanmalıdır.

Sonraki [P13 Editor roundtrip dilimi](UNITY_EDITOR_ROUNDTRIP.tr.md), bu corpus'un normal processing/export hattından çıkan iki prefab projesini gerçekten Editor'da açıp seçili alan/ref bağlantılarını doğruladı ve üç bozuk çıktı negatif kontrolü ekledi. Bu P09 raporundaki `NotRun` kendi yükleme testinin tarihsel kapsamını belirtir; yeni Editor kabul kanıtı ayrı raporlardadır.
