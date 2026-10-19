# Koleksiyon hata izolasyonu — P04 ilk dilim

Bu paket, yerel veri/decoder hatasında bağımsız kaynak ve export koleksiyonlarına devam eder. Hatalı koleksiyonun atlanması rapora yazılır ve genel export sonucu başarısız kalır. İptal, bellek, erişim ve disk hataları bu devam mekanizmasına alınmaz.

## Import davranışı

- `FileContainer.ReadContents`, bir serialized girdinin okunması başarısız olduğunda o girdiyi `FailedFile` olarak kaydeder. Aynı kapsayıcıdaki ve alt kapsayıcılardaki sağlam girdiler işlenmeye devam eder.
- `GameBundle.FromPaths` ve `SerializedBundle.FromFileContainer`, asset factory/nesne ekleme hatasını serialized koleksiyon sınırında izole eder. Yarım koleksiyon bundle'dan çıkarılır; dependency binding bu koleksiyona çözülmez. Başarılı koleksiyonların yüklenmesine devam edilir.
- Doğrudan `AddCollectionFromSerializedFile` API'si hata atmaya devam eder; bu çağrıda da yarım koleksiyon grafikten çıkarılır. Batch import ayrıca `FailedFile` kaydı üretir.
- Raw koruma açıksa, kurtarılabilir ilk factory hatasından sonra kalan nesneler factory'ye verilmeden diske kaydedilir ve `CollectionAborted` durumunu alır. Önceki başarıyla okunmuş nesneler de koleksiyonla birlikte grafikten çıkarılır; korunmuş özgün baytları depoda kalır. Bu nesnelerin `Parsed` durumu yalnız okuma sonucunu anlatır, grafiğe kabul edildikleri anlamına gelmez.
- `FailedFile` kayıtları `Stage` ve varsa `DeclaredObjectCount` içerir. `CreateAssetCollection` ile `ReadFile` ayrılır. `Bundle.FetchFailedFiles()` alt bundle hatalarını da getirir. Uygulama yükleme sonunda bu hataları loglar.

Özelliğin açılması için yeni ayar gerekmez. Raw baytların ayrıca korunması için önceki paketin `RawRecoveryDirectory` / `--raw-output` ayarı kullanılır.

## Export davranışı

Unity-project ve primary-content export iki yerel sınırı ayrı ele alır:

1. **Preparation:** exporter seçimi, koleksiyon oluşturma, üyelerinin listelenmesi, ad ve export edilebilirlik bilgisinin alınması. Üye listesi tamamen okunup doğrulanmadan kuyruğa işaret koyulmaz. Liste yarıda hata atarsa daha sonra bağımsız işlenebilecek üyeler yanlışlıkla kaybolmaz. Tekrarlı/önceden kuyruğa alınmış üyelerle çakışan koleksiyon reddedilir.
2. **Export:** koleksiyonun dosya üretimi. Kurtarılabilir exception veya `false` dönüşü kaydedilir; sonraki koleksiyon denenir.

Unity referans container'ı hazırlıkta doğrulanan üye listesini kullanır; hata atabilecek üyelik iterator'ını ikinci kez çalıştırmaz. Scene koleksiyonları özgün tipleriyle kalır. Hazırlıkta reddedilen nesne için mevcut missing-reference davranışı kullanılır.

Koleksiyon hata verse de Unity export'un normal post-export adımları çalışabilir. Import'ta kayıtlı bir `FailedFile` varsa, raw mod kapalı olduğunda da `LastExportSucceeded` başarısızdır. Primary-content yolu da aynı kuralı uygular.

## Rapor

Her iki export türü çıktı köküne `collection-export-report.json` yazar. `SchemaVersion` değeri `1` olan rapor şunları içerir:

| Alan | Anlamı |
|---|---|
| `ExportKind` | `UnityProject` veya `PrimaryContent` |
| `Completed` | Koleksiyon export döngüsü tamamlandı mı? |
| `AttemptedCollectionCount` | Export çağrısı denenmiş koleksiyon sayısı; hazırlık hataları ve export edilmeyen koleksiyonlar hariç |
| `ExportedCollectionCount` | Export çağrısı `true` dönen koleksiyon sayısı |
| `ImportFailures` | Kaynak adı/yolu, aşama, biliniyorsa bildirilen nesne sayısı ve diagnostic |
| `Failures` | Hazırlık/export hatası, koleksiyon adı, temsilci kaynak nesnesi, exception türü ve diagnostic |
| `Succeeded` | Döngü tamamlandı ve bu rapordaki import/koleksiyon hataları boş mu? |

`Failures` içindeki PathID/class ID/kaynak yolu, koleksiyon hazırlığını tetikleyen **temsilci nesneyi** tanımlar. Gruplanmış bir sahnenin bütün üyelerinin hata envanteri değildir. 64 bit PathID değerlerini okuyan tüketiciler tamsayı hassasiyetini korumalıdır.

Rapor, hazırlık tamamlandığında, export döngüsündeki her yerel başarısızlıktan sonra ve döngü sonunda yazılır. Fatal hazırlık hatasında disk raporu henüz oluşmamış olabilir. Fatal export hatasında önceki rapor `Completed: false` olarak kalır; sayaçları son yazma noktasına aittir. Atomik dosya değiştirme veya process crash garantisi bu dilimde yoktur.

`ProjectExporter.LastReport`, `PrimaryContentExporter.LastReport` ve `ExportHandler.LastCollectionReport` bellek içindeki son raporu sağlar. SystemTester aynı raporu vaka JSON'unun `CollectionExport` alanına ekler. Bir import/koleksiyon hatası exit `1` üretir; sağlam verinin çıktıya ulaşması bu durumu başarıya dönüştürmez.

Bu raporun başarı kapsamı **koleksiyon döngüsüdür**. Raw recovery'nin bekleyen/kısmi nesne kontrolü ve Unity post-export sonucu ayrıca değerlendirilir; genel sonuç için `LastExportSucceeded` veya SystemTester vaka sonucunu kullanın. `Completed`, Unity Editor import veya davranış doğrulaması değildir.

## Hata politikası ve sınırlar

- Ortak `ExceptionPolicy`, inner exception ve AggregateException üyelerini de inceler. `OperationCanceledException`, `OutOfMemoryException`, `UnauthorizedAccessException` ve `IOException` içeren hatalar yeni koleksiyon catch sınırlarında yutulmaz. Kaynak okuma sırasında `EndOfStreamException` bozuk girdi olarak izole edilebilir; export sırasında disk/stream hatası olarak yayılır.
- GZip/Brotli ve UnityFS blok/girdi okumasındaki mevcut catch'ler de kaynak okuma filtresini kullanır. Bu değişiklik tüm üçüncü taraf decoder'ların veya bütün uygulama katmanlarının exception davranışını doğruladığı iddiasını taşımaz.
- Export exception'ından önce yazılmış kısmi dosyalar silinmez veya geri alınmaz. Başka koleksiyonların başarısız çıktıya referansları kalabilir. Hatalı gruptan bağımsız nesneler ayrı kurtarılabilir; üretilen projenin bütünlüğü garanti edilmez.
- Factory/exporter'ın kendi paylaşılan durum değişiklikleri geri alınmaz. Global processor'lar, dependency discovery/provider çağrıları, post-exporter'lar ve UI event callback'leri bu dilimde genel catch ile atlanmaz. Bu aşamalardaki hatalar işlemi hâlâ durdurabilir.
- `ReadFile` öncesinde veya decoder içinde arşivlenemeyen dosyalar için raw koruma kapsamı genişletilmedi. Tam dosya envanteri, bağımlılık doğrulaması ve Editor oracle testleri açık.

## Doğrulama

| Kontrol | Sonuç |
|---|---|
| Tüm solution, Debug | **590/590 geçti**, 8 test projesi |
| Değişen alanlar, Release | **61/61 geçti**: 25 izolasyon, 1 container, önceki 35 ilgili test |
| Solution Release build | Başarılı; son incremental build 0 hata/uyarı. Önceki build'de yalnız mevcut sprite/animator TODO uyarıları vardı |
| CLI, yalnız sağlam girdi | Exit `0`; başarılı ve tamamlanmış koleksiyon raporu |
| CLI, sağlam + iki bozuk girdi, raw açık/kapalı | Sağlam payload çıktı, iki import hatası raporlandı; her iki koşul exit `1` |
| Raw payload doğrulaması | Dört nesne korundu, dosyaların SHA-256 değerleri eşleşti |
| Diff kontrolü | `git diff --check` başarılı |

Tam Release suite'in geçtiği iddia edilmiyor; başlangıç checkout'unda da bulunan Debug-assert test hatası [baseline notlarında](DECOMPILATION_BASELINE.tr.md) kayıtlı.

`CollectionIsolationTests`, loose/nested import, dependency quarantine, kalan ham baytların korunması, preparation/membership/duplicate/export/false sonuçları, aynı iterator'ın tekrar çalıştırılmaması, fatal/wrapped hata yayılımı ve raw kapalıyken import hatasının iki export sonucuna aktarımını kapsar. `FileContainerIsolationTests`, bozuk serialized type table'dan sonra sibling ve nested girdileri doğrular.

Fiziksel CLI denemesinde aynı işe sağlam TextAsset, bozuk object table ve yinelenen PathID içeren dosya verildi. Raw açık ve kapalı iki koşulda da sağlam dosya üretildi, iki import hatası JSON raporuna aktarıldı ve exit `1` döndü. Raw açıkken dört okunabilir nesnenin hash'leri doğrulandı; factory'den sonraki nesne `CollectionAborted` olarak kaydedildi. Mevcut çıktı/depo dosyaları korundu. Örnekler kaynak sahibi olduğumuz builder fixture'larıdır; Unity Editor golden corpus değildir.

```bash
dotnet test AssetRipper.slnx --logger trx --results-directory /tmp/assetripper-results
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj -c Release --filter 'FullyQualifiedName~CollectionIsolationTests'
dotnet test Source/AssetRipper.IO.Files.Tests/AssetRipper.IO.Files.Tests.csproj -c Release --filter 'FullyQualifiedName~FileContainerIsolationTests'
```

Önceki paketler: [baseline](DECOMPILATION_BASELINE.tr.md), [raw recovery](RAW_RECOVERY.tr.md). Sonraki temel kalite işi: P06 discovery/manager/cache/resolver boyunca assembly kimliği ve çakışmalarının korunması.
