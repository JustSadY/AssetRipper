# Ham nesne koruması — P05 ilk dilim

Bu paket, başarıyla okunmuş `SerializedFile` nesnelerinin özgün baytlarını asset factory çalışmadan önce kalıcı diske kaydeder. Factory `null` döndürdüğünde, nesne tipi bilinmediğinde veya MonoBehaviour alanları okunamadığında bu baytlar sonraki kurtarma denemeleri için elde kalır. Yeni bir decompiler veya native metot kurtarma motoru içermez.

## Kullanım

Özellik varsayılan olarak kapalıdır. Web arayüzünde **Ayarlar → Ham Veri Kurtarma Klasörü** alanını dosyaları yüklemeden önce doldurun. Sonradan yapılan değişiklik önceden yüklenmiş nesneleri arşivlemez; yeniden yükleme gerekir. Boş veya yalnız boşluk içeren değer korumayı kapatır.

SystemTester:

```bash
dotnet run --project Source/AssetRipper.Tools.SystemTester -- \
  /path/to/Game \
  --output /path/to/Export \
  --raw-output /path/to/Recovery \
  --report /path/to/result.json
```

API üzerinden `FullConfiguration.ImportSettings.RawRecoveryDirectory` değerini `ExportHandler.Load` çağrısından önce ayarlayın. Ham veri klasörünü giriş dosyalarının tarandığı dizinin dışında tutun. Her yükleme kendi `Recovery_<GUID>` alt klasörünü oluşturur; aynı kökteki önceki işler silinmez.

```text
Recovery/
  Recovery_<GUID>/
    objects.jsonl
    summary.json
    Objects/
      <sha256>.bin
Export/
  raw-recovery.json
```

`raw-recovery.json`, sayaçları ve kalıcı kurtarma klasörünün mutlak yolunu taşır. Baytlar export klasörüne ikinci kez kopyalanmaz. Çıktıyı başka makineye taşırken ilgili `Recovery_<GUID>` klasörünü de taşımak ve kayıtlı mutlak yolları buna göre yorumlamak gerekir. SystemTester, yükleme tamamlanmışsa aynı özeti vaka raporunun `RawRecovery` alanına da ekler; normal modda bu alan `null` olur.

## Kayıt sözleşmesi

`objects.jsonl` UTF-8 JSON Lines biçimindedir; her satırın `SchemaVersion` değeri `1` olur. Koleksiyon kaydı kaynak yolu, özgün/normalize edilmiş ad, kapsayıcı adı, Unity sürümü, serialized format, platform, endian ve TypeTree varlığını içerir. Nesne kaydı PathID, class/type ID, serialized type/script indeksi, biliniyorsa offset ve bildirilen uzunluğu içerir.

`ByteOffset`, **açılmış serialized dosya akışının** başından itibaren konumdur (`OffsetSpace: "SerializedFile"`). Sıkıştırılmış bundle'ın fiziksel disk konumu değildir. Program içinde oluşturulmuş nesnelerde offset bilinmeyebilir. PathID gibi 64 bit JSON tamsayılarını okuyan tüketiciler tamsayı hassasiyetini korumalıdır.

Her nesne için sıralama:

1. `ObjectDiscovered`: iş içi nesne kimliği, kaynak bilgisi, SHA-256 ve mevcut bayt uzunluğu yazılır.
2. Özgün nesne baytları `Objects/<sha256>.bin` dosyasına yazılıp flush edilir. Aynı içerik tek dosyada tutulur; mevcut dosya uzunluk/hash denetiminden geçer.
3. `ObjectPreserved`: payload yolu ve `Captured` durumu yazılır; ancak bundan sonra factory çağrılır.
4. `ObjectRead`: okuma sonucu ve varsa hata bilgisi yazılır. Tembel MonoBehaviour okumasının sonraki sonucu aynı nesne kimliğiyle eklenir.

Koleksiyon ve nesne kimlikleri **yalnız o yükleme işi içinde** benzersizdir. Aynı PathID'ye sahip iki koleksiyonun kayıtları birleştirilmez. İçerik hash'i nesne kimliği yerine kullanılmaz.

| Durum | Anlamı |
|---|---|
| `Captured` | Baytlar korundu; factory sonucu henüz yok |
| `Deferred` | MonoBehaviour alan okuması sonraya bırakıldı |
| `Parsed` | Mevcut parser okumasını tamamladı |
| `FactoryReturnedNull` | Factory nesne üretmedi |
| `UnknownType` | Desteklenmeyen class/type için UnknownObject üretildi |
| `Unreadable` | Parser okunamayan nesne fallback'i üretti |
| `Partial` | Header/alan okuması kısmi kaldı veya payload ile şema uyuşmadı |
| `FactoryException` | Factory'den hata çıktı; baytlar daha önce korunmuştu |
| `CollectionAborted` | Aynı koleksiyondaki önceki hata nedeniyle factory'ye verilmeyen nesnenin baytları korundu; P04 dilimiyle eklendi |

`Parsed`, alan değerlerinin oracle ile karşılaştırıldığı veya Unity Editor'de doğrulandığı anlamına gelmez. İşlenen/sentetik engine nesneleri yeniden arşivlenmez; sayaçlar bu hook'a ulaşan kaynak nesnelerine aittir.

`summary.json`, import/process/export kontrol noktalarında yenilenir. `DiscoveredObjectCount` ile `PreservedObjectCount` farklıysa veya başarısız/bekleyen nesne varsa `HasIncompleteObjects` doğrudur. Raw koruma açıkken bu durum Unity-project ve primary-content export'un `LastExportSucceeded` sonucunu başarısız yapar. SystemTester bunu exit `1` ile bildirir. Ham baytların korunması, eksik parse'ı başarılı sonuca dönüştürmez.

## Dayanıklılık ve sınırlar

- Ham kayıt yazımı başarısızsa ilgili nesnenin factory okumasına geçilmez. Journal yazma hataları MonoBehaviour parse hatası olarak yutulmaz. Dosya akışlarında `Flush(true)` kullanılır; bu ACID işlem veya elektrik kesintisinde tam dosya sistemi garantisi değildir.
- Journal her olayda açılıp sona eklenir ve kapatılır. İş çökerse önceki tamamlanmış satırlar ve payload'lar kalır; son satır yarım kalabilir. `summary.json` son kontrol noktasını temsil eder; yarıda kesilen export'un referans dosyası da güncel olmayabilir. İncelemede tamamlanmış journal olayları esas alınır. Otomatik journal replay/resume bu dilimde yoktur.
- Hook, `SerializedFile` okunduktan sonra çalışır. Bundle/decompression/object table aşamasında okunamayan dosyaları arşivlemez. Sonraki [P04 dilimi](COLLECTION_ISOLATION.tr.md), kurtarılabilir factory hatasından sonraki nesnelerin baytlarını da korur; fatal hata nedeniyle hook'a ulaşmayan nesneler kapsam dışıdır. Dosya düzeyinde eksiksiz keşif envanteri henüz yoktur.
- Özgün bundle'ın bütünü, harici `.resS`/audio stream'leri, tam TypeTree/reference-type şemaları ve bütün kaynak dosyanın hash'i bu depoya alınmaz. Bu nedenle depo tek başına tam proje rekonstrüksiyonu için yeterli değildir.
- Kalıcı payload kopyası ve olay başına disk flush ek IO/disk maliyeti getirir. Zero-copy, MMF, paralel export veya bellek tasarrufu iddiası yoktur; performans benchmark'ı henüz yapılmadı.
- P04 koleksiyon hata izolasyonunun ilk dilimi [ayrı pakette](COLLECTION_ISOLATION.tr.md) uygulandı. Kalıcı kaynak kimliği/dosya envanteri, tam şema/ref doğrulaması ve bağımsız kurtarma worker'ı açık.

## Doğrulama

Kaynak sahibi olduğumuz fixture'lar kullanıldı; Unity Editor'den üretilmiş corpus veya davranış doğrulaması olarak sunulmuyor.

| Kontrol | Sonuç |
|---|---|
| Tüm solution testleri, Debug | **564 / 564 geçti**, 8 test projesi |
| Değişen alanların testleri, Release | **35 / 35 geçti**: 16 raw recovery, 2 offset, önceki 13 decompile/dizin, 4 GUI/headless |
| Solution Release build | Başarılı; mevcut animator TODO uyarısı dışında yeni uyarı yok |
| Disk üzerinde CLI, geçerli serialized TextAsset | Exit `0`; payload hash/offset, sayaçlar, export referansı ve vaka raporu eşleşti |
| Aynı CLI girdisinin tekrarı | Ayrı recovery işi ve export alt klasörü; mevcut sentinel dosyalar korundu |
| Disk üzerinde CLI, bilinmeyen tip | Exit `1`; özgün baytlar korundu, `UnknownType` ve başarısız sayaç kaydedildi |
| CLI, raw seçenek verilmeden | Exit `0`; `RawRecovery: null`, raw referansı/deposu oluşturulmadı |
| Diff ve localization JSON kontrolü | Başarılı |

Tam Release suite çalıştırılmadı; başlangıç checkout'unda da bulunan Debug-assert test hatası [ilk paket notlarında](DECOMPILATION_BASELINE.tr.md) kayıtlıdır.

- `RawRecoveryTests`: factory'nin baytları değiştirmesinden önce koruma, null/exception sonucu, hash ile dedup ve bozulma tespiti, bilinmeyen/okunamayan tip, bozuk header, TypeTree ile başarılı/kısmi okuma, tembel MonoBehaviour hatasının aynı kayda bağlanması, disk/journal hata yayılımı, kapalı mod ve iki export yoluna eksik sonuç aktarımı.
- `ObjectInfoSourceTests`: little/big endian serialized dosyalarda offset ve uzunluğun özgün payload'a karşı doğrulanması; nesne eşitliği ve wire formatı korunuyor.
- `GameFileLoaderTests`: GUI/headless × Unity/primary-content yollarının dördünde mevcut dosyanın korunması ve disk üzerindeki kurtarma günlüğüne doğru referans.

```bash
dotnet test AssetRipper.slnx --logger trx --results-directory /tmp/assetripper-results
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj -c Release --filter 'FullyQualifiedName~RawRecoveryTests'
dotnet test Source/AssetRipper.IO.Files.Tests/AssetRipper.IO.Files.Tests.csproj -c Release --filter 'FullyQualifiedName~ObjectInfoSourceTests'
dotnet test Source/AssetRipper.GUI.Web.Tests/AssetRipper.GUI.Web.Tests.csproj -c Release --filter 'FullyQualifiedName~GameFileLoaderTests'
```

İlk paketin baseline/ortam notları: [DECOMPILATION_BASELINE.tr.md](DECOMPILATION_BASELINE.tr.md). İş sırası: [DECOMPILATION_ROADMAP.tr.md](DECOMPILATION_ROADMAP.tr.md).
