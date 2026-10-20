# P13 — Çoklu assembly, asmdef ve DLL script bağları

## Kapsam

Kaynak sahibi olduğumuz **Unity 6000.4.0f1 / Linux64 / Mono** corpus'u üç assembly taşır:
`AssetRipper.GraphModel` → ortak serializable veri, base component ve interface;
`AssetRipper.GraphComponents` → bunları kullanan component;
`Assembly-CSharp` → custom component'e ve ortak veri tipine başvuran köprü component.
Unity kaynak sahneyi ve prefab'ı yeniden açıp bağımsız sabitlerle doğrulamadan corpus
başarılı sayılmaz. Kaynak, üç DLL, iki TypeTree modu ve oracle **36 SHA-256 girdisiyle**
korunur. Orijinal source prefab/sahne, AssetRipper import girdisi olarak kullanılmaz.

## Düzeltilen yanlış başarı

Gerçek corpus'un bellekteki `AssetRipper.GraphComponents` metadata'sına, hiçbir C# üyenin
kullanmadığı `Missing.Unused.Dependency, Version=1.0.0.0` referansı eklendi. Özgün corpus
dosyaları değiştirilmedi. Düzeltme öncesi C# üretimi ve job sonucu başarılıydı; asmdef ise
üretilmemiş bir assembly'ye başvuruyordu. Regresyon **Expected False / Actual True** ile
bu yanlış başarıyı gösterdi.

Artık script exporter fiilen ürettiği source/DLL çıktılarının listesini tutar. Asmdef
oluştururken:

- Üretilmiş custom source assembly → `references`.
- Kaydedilmiş DLL → `precompiledReferences` ve `overrideReferences=true`.
- Üretilmemiş assembly → ilişki artifact'ta korunur ve `MissingAssemblyArtifact`.
- Source olarak üretilmiş predefined assembly'ye asmdef referansı →
  `PredefinedAssemblyCannotBeReferenced`; geçersiz asmdef referansı yazılmaz.
- Bilinen Unity/framework referansları mevcut reference registry yolunda kalır.

`script-export-report.json` yeni `UnityAssemblyReferenceFailures` listesini taşır:
`AssemblyName`, normalize `ReferenceIdentity`, `Reason`. Liste doluysa script sonucu
ve job sonucu başarısızdır; başarılı C# decompilation sonuçları aynen ayrı görünür.
Eksik DLL/paket uydurulmaz. Bu kontrol artifact varlığını ve referans türünü denetler;
tam CLR ABI, paket uyumu veya tüm asmdef kurallarının doğrulandığı anlamına gelmez.

DLL ve asmdef referans alanlarının ayrımı Unity'nin [Assembly Definition format
belgesine](https://docs.unity3d.com/6000.0/Documentation/Manual/assembly-definition-file-format.html)
uyar. Mevcut üç parametreli writer çağrısı korunur; normal Unity script export'u
çıktı envanterini taşıyan yeni overload'u kullanır.
Predefined assembly ve cycle kısıtları Unity'nin [referans kurallarında](https://docs.unity3d.com/6000.0/Documentation/Manual/assembly-definitions-referencing.html)
açıklanır; cycle denetimi bu değişiklikte uygulanmadı.

## Kabul deneyi

- İki TypeTree modu × external bundle var/yok: inherited/private/ortak tip alanları ve
  **12 PPtr bağı**. External eksikken serialize edilmiş ID'ler değişmez.
- İki şema × Decompiled/Hybrid: normal `ExportHandler.Process/Export` sonucunda çıkan
  projeler gerçek Editor'de açılır; source C# derlenir, missing-script yoktur, alanlar
  ve local/external/null/component referansları eşleşir.
- Her pozitif Editor vakasında `Evaluate` metotları için altı sonuç, interface dispatch
  üzerinden üç ek sonuç: **9 gözlem/vaka**. Bunlar yalnız bilinen kaynak fixture metotlarıdır.
- Decompiled modda iki custom asmdef ve yalnız source script'ler; Hybrid modda iki saved
  custom DLL ve decompile edilmiş `Assembly-CSharp`. Orijinal `Assembly-CSharp.dll`
  Assets'e verilmez. Validator fixture DLL'sine compile-time bağımlılık taşımaz.
- Gerçek Unity, Hybrid `GraphNode` MonoScript'ini DLL içinde **fileID 2126133806** ile
  çözdü; source modda **11500000**. GUID, sınıf/namespace/assembly ve `m_Script` nesne
  eşitliği ayrıca denetlenir.
- Negatifler: asmdef model referansı silinir; Hybrid model DLL'si silinir; DLL script
  fileID'si yanlışlıkla 11500000 yapılır. İlk ikisinde gerçek compiler error / nonzero
  exit ve `SemanticValidation=NotRun` gerekir; üçüncüsünde checker `MissingScript` verir.
- Corpus gerektirmeyen kontroller: hash/path/eksik DLL/oracle/manifest üyeliği ve dört
  asmdef referans sözleşmesi. Ek corpus regresyonu kullanılmayan eksik metadata referansı
  için C# üretimi başarılıyken Unity artifact kontrolünün başarısızlığını doğrular.

PluginImporter'ın ham Editor enabled alanı, Any Platform ve exclude ayarlarıyla birlikte
ölçüldü. Bu corpus'ta `AnyPlatform=true / Editor=false / ExcludedEditor=false` olan DLL'ler
Editor'de yüklendi ve bütün pozitif oracle'ları sağladı. Yalnız tek YAML bayrağına bakarak
uyumsuzluk sonucu çıkarılmadı. API ayrımı [Unity PluginImporter
belgesinde](https://docs.unity3d.com/cn/6000.0/ScriptReference/PluginImporter.html) de yer alır.

## Doğrulama ve kanıt

Kod sonrası ilk 17 corpus/Editor/manifest kontrolü geçti. Kullanılmayan eksik referans
regresyonu önce yanlış başarıyı gösterdi. Dört yeni deterministik asmdef testinin ilk
turunda fixture modülünün otomatik mscorlib girdisi ayrıştırılmadığı için test setup'ı
düzeltildi; üretim framework filtresi gevşetilmedi. Son kodla phase sonu doğrulaması tamamlandı:

| Koşu | Sonuç |
|---|---|
| Tam solution Debug, 8 test projesi | **982/982** |
| Tam solution Release, 8 test projesi | **981/981** |
| Yeni `UnityAssemblyGraph` kategorisi | **22/22 Debug ve Release** |
| Son solution Release build | **0 uyarı / 0 hata** |

Release'te yalnız bilinen
`ExportIdHandlerTests.GetMainExportID_ValueGreaterThan100000_DebugAssertFails`
dışarıda bırakıldı. İki koşunun vaka kümeleri yalnız bu testte ayrılıyor.

Corpus yokken **10 geçti / 12 NotExecuted**; corpus ve pinned referanslar varken yalnız
Editor executable yoksa **15 geçti / 7 NotExecuted**. Yanlış yapılandırılmış bir fixture
veya başarısız başlatma skip'e çevrilmez.

Debug ve Release için ayrı ayrı **432 corpus kopyası hash'i**, **119 import öncesi
snapshot hash'i**, **8 gerçek script GUID/fileID bağı**, **48 reader PPtr kenarı**,
**36 Editor davranış gözlemi** ve dört beklenen negatif sonuç doğrulandı. Özgün 36
corpus girdisinin hash'leri değişmedi. Son kaynak hash'leri, TRX, yanlış başarıyı
gösteren ilk regresyon, ara test setup hataları ve artifact denetimleri [yerel phase
kanıtında](../Source/0Bins/TestResults/P13-AssemblyGraph-20261020/phase-verification.json)
korundu. Native Editor retry gerekmedi; SDK/paket yükseltilmedi ve commit atılmadı.

İlk corpus kurulumundaki iki oracle hatası korundu: Unity'nin deserialize ettiği null
object slot'ları CLR `SequenceEqual` ile karşılaştırılıyordu. Kaynak ve checker, Unity
object eşitliğiyle karşılaştırıyor. Üçüncü, ayrı output dizinindeki üretim bağımsız
scene/prefab oracle'ını geçti. Bu bir exporter düzeltmesi değildir; native retry eklenmedi.

Kanıtlar `Source/0Bins/Other/AssetRipper.Tests/{Debug,Release}/TestResults/unity-assembly-graph/`
altında: corpus manifest/oracle, import öncesi Assets/Packages/ProjectSettings snapshot'ı,
mutation'ın özgün dosyası/hash'i, script/collection raporları ve gerçek Editor JSON'ları.
CI yalnız JSON ve seçilmiş `Evidence` ağacını alır; tam log ve Library cache'leri alınmaz.
Uzak CI çalıştırılmadı.

Üretim komutu ve ortam değişkenleri: [fixture README](../TestProjects/AssemblyGraph/README.md).

## Açık sınırlar

Bu dilim tek sürüm/platform Mono grafiğini kapsar. Kaybolan özgün asmdef GUID'leri,
platform/define/version constraints, üçüncü taraf paket sürümleri, cycle/ABI denetimi,
geniş sürüm matrisi, Play Mode ve IL2CPP bu kabulün parçası değildir. Source/DLL karışık
referans alanı writer sözleşmesiyle sınandı; genel karma asmdef/DLL proje matrisi ayrıca
Editor kanıtı gerektirir. P12'nin gerçek 6000.5/v23 kapısı açık kalır.
