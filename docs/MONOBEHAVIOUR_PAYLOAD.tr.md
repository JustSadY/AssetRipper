# P09 — MonoBehaviour payload ve PPtr doğrulaması, ilk dilim

Bu dilim iki somut kaybı giderir: başarısız script alan okumasından sonra payload'ın nesneden düşmesi ve doğru okunmuş PPtr alanlarının dependency yürüyüşünde görünmemesi. TypeTree ve assembly şeması yolları için bağımsız yazılmış bayt/alan/PPtr oracle'ı eklendi. Bu bir **kontrollü fixture** doğrulamasıdır; Editor'den üretilmiş sahne veya AssetBundle kabul testi değildir.

## Düzeltilen davranış

Önceden `UnloadedStructure.LoadStructure()` ve TypeTree okuma hatası `Structure=null` yapıyordu. Ham nesne koruması kapalıysa okunamayan script baytlarının elde kalan nesne üzerindeki referansı da kayboluyordu. Dependency yürüyüşünün boş sonucu, bilinmeyen alanlarla gerçekten boş bir yapıyı ayırt etmiyordu. İlk regresyon testi bu `null` sonucuyla başarısız oldu.

Artık başarısız okuma `UnreadableStructure` bırakır. Bu nesne şunları tutar:

- Özgün, okunamayan bayt segmenti; tahmin edilmiş alanlar yayımlanmaz.
- `SerializableStructureReadResult`: tüketilen bayt sayısı, beklenen segment uzunluğu ve diagnostic.
- Şema kaynağı (`Assembly`, `TypeTree` veya `Header`), payload kapsamı ve biliniyorsa nesne içindeki başlangıç offset'i.
- `DependencyCoverage: "Incomplete"`: boş dependency listesi tamlık iddiası taşımaz.

Normal `LoadStructure()` extension'ı bu durumda yine `null` döndürür; `Structure` artık kayıp kanıtını taşıyan nesnedir. Tekrar çağırmak aynı başarısız alanları yeniden okumaz veya journal'a aynı sonucu tekrar yazmaz. Clone işlemi kanıtı korur. Okunamayan yapının binary `WriteEditor` / `WriteRelease` işlemi desteklenmez; sahte script gövdesi yazılmaz. YAML yürüyüşünde eksik alanlar uydurulmaz ve export raporu başarısız kalır.

Başlık okunabildiyse segment kapsamı `ScriptFields` ve offset başlık sonudur. Başlık bile okunamadıysa `PayloadScope: "WholeObject"`, offset `0` olur ve mevcut nesne baytlarının tamamı korunur. Bu offset, serialized nesnenin başlangıcına göredir; bundle offset'i değildir. Manuel oluşturulan lazy yapıda kaynak offset'i bilinmiyorsa `null` kalır. Version-changer lazy yapıyı yeni MonoBehaviour'e bağlarken offset ve raw snapshot bağlantısını korur.

## PPtr dependency kaybı

`SerializableValue.FetchDependencies` önceden complex alanın `FetchDependencies()` metoduna yöneliyordu. Üretilmiş PPtr subclass'ları kendi başlarına dependency üretmediğinden tek pointer, pointer dizisi ve nested yapıdaki pointer'lar listeden düşüyordu. Oracle'da scalar ve pointer değerleri doğruyken dependency listesi sıfır öğeydi.

Şimdi `IPPtr` doğrudan `(FileID, PathID)` olarak alınır; nested yapılar için mevcut recursive yürüyüş korunur. Alan yolları `direct`, `links[0]` ve `nested.target` biçimindedir. Null ve çözümlenemeyen pointer değerleri de korunur; hedef bulunamadığında sıfıra çevrilmez. Bu, hedefin mevcut veya doğru asset olduğunu tek başına kanıtlamaz.

## Export raporu ve ham fallback

Her iki export yolu, koleksiyonları hazırlamadan önce lazy MonoBehaviour okumalarını tamamlamayı dener. `collection-export-report.json` schema 1'e `MonoBehaviourFailures` listesi eklendi. Bu liste boş değilse `CollectionExportReport.Succeeded` ve üst export sonucu `false` olur; bağımsız asset'lerin export'u devam eder. SystemTester mevcut `CollectionExport` kaydı üzerinden aynı hataları taşır.

| Alan | Anlam |
|---|---|
| `SourceCollection`, `SourcePath`, `PathId` | Hatanın görüldüğü koleksiyon ve nesne; PathID dosyası içinde anlamlıdır |
| `SchemaSource` | `Assembly`, `TypeTree` veya başlık okuması için `Header` |
| `PayloadScope`, `ObjectByteOffset` | Segmentin kapsamı ve kaynak nesne içindeki bilinen başlangıcı |
| `BytesRead`, `ByteLength` | Denemenin tüketimi ve korunan segment uzunluğu |
| `PayloadPath`, `Sha256` | Export köküne göre `.bin` yolu ve tam segment hash'i |
| `DependencyCoverage` | Bu başarısız okumada `Incomplete` |
| `Diagnostic` | Şema çözümleme, okuma veya uzunluk uyuşmazlığı nedeni |

Başarısız segmentler export kökündeki `MonoBehaviourRecovery/<sha256>.bin` dosyalarına yazılır. Aynı baytlar tekrar kullanılabilir; mevcut farklı içerik veya dosya/dizin çakışması ezilmez ve hata dışarı taşınır. Bu fallback **başarısız MonoBehaviour okumaları için ham capture ayarı kapalıyken de çalışır**. Normal okunan nesneler için genel raw arşivleme açmaz. Başlık hatası dışındaki durumda yalnız script alan segmenti saklanır; tüm kaynak dosya ve harici resource stream'leri arşivlenmiş sayılmaz.

P05 raw capture açıksa tam özgün nesne baytları ayrıca kendi kalıcı SHA deposunda kalır; aynı ObjectId için `Deferred → Parsed/Partial` journal sonucu güncellenir. Segment fallback'i bunun yerine geçmez. Extra segment kopyası disk maliyeti oluşturabilir; büyük oyunlar için streaming/RSS ölçümü bu dilimde yapılmadı.

Okuma sınırları ortak exception politikasını kullanır. Beklenmeyen girdi sonu (`EndOfStreamException`) layout hatası olarak kaydedilir. IO, iptal, yetki ve bellek hataları, wrapped/aggregate biçimleri dahil, dışarı taşınır. Journal status yazımı kaynak-okuma catch sınırının dışındadır; journal IO hatası bozuk script alanı gibi gösterilmez.

## Oracle ve regresyon kapsamı

`MonoBehaviourPayload` NUnit kategorisinde bağımsız bayt yazıcısı `BinaryPrimitives` kullanır; `SerializableStructure.Write` ile üretilip aynı okuyucuyla doğrulanan bir döngü değildir. Sabit C# 9 kaynak fixture'ı P07'nin 167 hash-pinned .NET 10 referans image'ıyla derlenir. Metadata resolver'a aynı referanslar açık RuntimeContext ile verilir. Fixture'ın `UnityEngine` adları kaynak sahibi olduğumuz küçük **shim** sınıflarıdır; gerçek Unity DLL'si veya runtime'ı değildir. Fixture DLL'si yürütülmez.

İki bağımsız şema aynı script baytlarına uygulanır: elle kurulmuş TypeTree ve gerçek assembly metadata'sından `FieldSerializer` ile çıkarılan şema. Şunlar elle yazılmış oracle ile kontrol edilir:

- Kalıtımla gelen `inherited=-17`, private `[SerializeField] short hidden=-1234`, `enabled=true`, UTF-8 `label="Merhaba ☃"`, `values=[7,-9,42]`, nested `count=91`.
- `[NonSerialized]`, static ve readonly alanların serialize edilen şemaya girmemesi.
- Yerel asset, harici dependency, null pointer, eksik dependency ve 64-bit `PathID=4294967297`; altı beklenen pointer ve alan yolu, mevcut iki hedefin nesne kimliğiyle eşleşmesi.
- TypeTree okuyucusunda little/big endian ve byte/short/string alignment. İki factory/şema yolunda release header ve script alanları birlikte okunur.
- Kesik payload, fazla son bayt ve negatif array uzunluğu: iki şema yolunda ham baytlar korunur; yarım alanlar yayımlanmaz.
- Eksik script, kesik header, tekrar okuma/clone, layout sırasında fatal hata, schema-resolution hatası, journal IO ve mevcut recovery çıktısıyla çakışma sınırları.
- İki gerçek export giriş noktası: raw capture kapalı, hatanın tek nedeni `MonoBehaviourFailures`, genel sonuç başarısız ve bağımsız TextAsset diske ulaşmış.

```sh
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter TestCategory=MonoBehaviourPayload --logger trx
```

Kaynak, fixture DLL'si, script alan baytları ve `payload-verification.json` test output dizinindeki `TestResults/monobehaviour-payload/<Assembly|TypeTree>-<uuid>/` altında saklanır. Rapor referans manifestini, hash'leri, gözlenen alan değerlerini ve pointer yollarını taşır. İki export yolunun başarısızlık raporu ve `.bin` kanıtları da ayrı alt dizinlerdedir. JSON dosyaları NUnit attachment'tır; dizinin tamamı CI artifact yoluna eklendi. `EditorGeneratedPayload=false`, `UnityEditorValidation=NotRun` açık kalır. Uzak CI çalışması burada tetiklenmedi.

## Yerel doğrulama sonucu

| Kontrol | Sonuç |
|---|---|
| Yeni payload/PPtr testleri | **28/28 geçti**, Debug ve Release runner'larında |
| Tüm solution, Debug | **698/698 geçti**, 8 test projesi; önceki gerçek Unity referans profili etkin |
| İlgili Release testleri | **169/169 geçti**, core 162 + IO 3 + GUI 4 |
| Solution Release build | Başarılı; **0 hata**, değişmeyen Animator dosyasındaki **1 mevcut TODO uyarısı** |
| Bağımsız kanıt denetimi | Dört şema raporunun DLL/payload hash'leri, altı değer ve altı PPtr yolu eşleşti; `struct.unpack` ile seçili binary offset'ler ayrıca doğrulandı |
| Export hata/fallback kanıtı | Debug/Release × iki yolun JSON başarısızlığı ve `.bin` hash'i ayrıca doğrulandı; diğer export hataları listesi boş |
| Editor-generated fixture / Unity Editor / IL2CPP | **NotRun** |

Bu ortamda build/test için `--no-restore -p:AllowMissingPrunePackageData=true` kullanıldı; SDK/paket sürümleri değiştirilmedi. İlgili Release sonuçları tüm Release suite'in geçtiği anlamına gelmez; mevcut Debug-assert test sınırı [baseline notlarında](DECOMPILATION_BASELINE.tr.md) kayıtlıdır. Önceki gerçek Unity compile testlerinin etkin olması bu yeni kontrollü payload fixture'ını Editor doğrulamasına dönüştürmez.

## Açık kabul kapıları

Tam bayt tüketimi yalnız layout kontrolüdür; aynı uzunlukta yanlış şema hâlâ yanlış değer verebilir. Ürün her alanı oracle ile karşılaştırmaz ve bu rapor bütün dependency hedeflerini doğrulamaz. Mevcut `Structure=null` olan custom/harici factory nesnelerinin nedeni geriye dönük tahmin edilmez; bu kayıtlar yeni factory/lazy hata yollarında üretilir.

P09'un Editor'den üretilmiş sahne/AssetBundle ve alan/PPtr referans raporu, gerçek Mono compiler çıktıları, managed-reference registry kimlik/graph oracle'ı, nested array/map edge case'leri, belirsiz şema cache'i ve tüm Unity sürüm/serialize modları açık. P13 GUID/fileID ve Editor import/sahne kabulü ile IL2CPP recovery tamamlanmış sayılmaz.

Sonraki [gerçek Editor corpus dilimi](UNITY_EDITOR_PAYLOAD.tr.md), 6000.4.0f1 / Linux64 prefab bundle'ında TypeTree ve assembly şeması için alan/PPtr hedef kontrollerini ekledi. Yukarıdaki tablo bu ilk kontrollü fixture diliminin tarihsel sonucudur. Managed-reference graph, başka profil/sürümler ve çıkarılan projenin Editor import'u ayrı kapılar olarak devam eder.
