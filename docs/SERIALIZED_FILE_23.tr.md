# SerializedFile 23 — P12

## Durum

| Kapsam | Kanıt durumu |
|---|---|
| Format 23 okuma/yazma, bilinmeyen sürümde açık hata, extracted TypeTree raporu | **Sentetik phase sonu testleri tamamlandı.** P12 Debug/Release **45/45**; tam regresyon Debug **960/960**, Release **959/959** |
| Gerçek Unity 6000.5 repro'su | **Yapılmadı.** Kullanıcı kararıyla Unity 6000.5 kurulmadı. Roadmap'in gerçek-repro kabul kapısı açık |

Sentetik testler burada uygulanan düzenin ve mevcut okuma/export yollarının
tutarlılığını gösterir. Unity'nin gerçek 6000.5 dosya düzenini doğrulamaz.

## Sorun

Unity 6000.5.0a5 ve sonrası SerializedFile format sürümü **23** yazar; 6000.4.6f1'e
kadar sürüm 22. Bu checkout'taki bütün kaynak sahibi corpus'lar (6000.4.0f1) sürüm
22 taşıyor.

Mevcut kod 23'ü tanımıyordu. `SerializedFileHeader.IsSerializedFileHeader`, enum'da
tanımlı olmayan sürüm için `false` döndürüyordu; dosya serialized file sayılmıyor,
sıradan bir kaynak dosyası olarak geçiyordu. Hata ortaya çıkmıyordu:
[upstream #2304](https://github.com/AssetRipper/AssetRipper/issues/2304) logunda
6000.5.2f1 oyunu "Exporting to Unity version 0.0.0a0" ile hiçbir asset vermeden
"başarıyla" bitiyor.

Format bilgisi iki kaynaktan geliyor:

- **Issue #2304:** 6000.5.2f1, Mono.
- **Bakımcının `serialized-file-version-23` dalı:** tek commit, 10 Ekim 2026, kendi
  ifadesiyle "Preliminary". Yalnız `FormatVersion`, `SerializedTypeBase` ve `TypeTree`
  dosyalarını değiştiriyor. Bu checkout'a kopyalanmadı; aynı düzen burada ayrıca
  uygulandı.

## Değişiklikler

1. **Bilinmeyen sürümde açık hata.**
   - Tanıma: 23'ten büyük ve 64'e kadar olan bir sürüm, v22 header düzeniyle
     denetlenir (0x14'teki metadata boyutu ≥ 13, 0x18'deki dosya boyutu = gerçek
     boyut). Tutuyorsa dosya serialized file olarak tanınır.
   - Okuma: `UnsupportedSerializedFileVersionException` fırlatılır; mesajda sürüm ve
     desteklenen en yüksek sürüm (23) yer alır. Bu istisna `IOException` değildir, P04
     politikasına göre kurtarılabilir girdi hatasıdır.
   - Sonuç: dosya `FailedFile` olur; `Logger.Error` ile loglanır ve
     `collection-export-report.json` içinde `ImportFailures` olarak sayılır.
   - Sınır: 64 üstü sürümler ve eski, tanımsız sürümler (ör. 4) eskisi gibi
     tanınmaz.
2. **Format 23 SerializedType.** TypeTree'li dosyada her tip için önce `Hash128`, sonra
   `int32` boyut okunur.
   - Boyut 0: TypeTree dosyanın **dışında** tutuluyor (`IsTypeTreeExtracted`), `OldType` boş.
   - Boyut 0 değil: ağaç satır içinde okunur ve kapladığı bayt `InlineTypeTreeByteCount`
     olarak kaydedilir. Kayıtlı boyutun neyi kapsadığı bilinmediği için eşitlik
     zorlanmıyor.
   - Negatif boyut reddedilir.
   - Hash (upstream'e göre xxh3_128) taşınır, doğrulanmaz.
   - Tip bağımlılıkları upstream'deki gibi iki durumda da okunur.
3. **TypeTree header'ı.** Format 23 ağacı sayısal `0x7474686D` sabiti ve format
   sürümüyle başlar. Her ikisi dosyanın endianness'iyle okunur/yazılır; sabitin little
   endian baytları `6D 68 74 74`, big endian baytları `74 74 68 6D` olur. Gerçek Unity
   bayt sırası henüz doğrulanmadı. Yanlış magic veya uyuşmayan sürüm reddedilir.
4. **Yazıcı.** Okuma ile simetrik. Kayıtlı boyut, burada yazılan ağaç bloğunun
   (`"tthm"` dahil) boyutudur. Unity'nin aynı anlamı kullandığı doğrulanmadı.
5. **Extracted tiplerin nesneleri.** Boş `OldType` ile
   `TypeTreeNodeStruct.TryMakeFromTypeTree` başarısız olur ve mevcut TypeTree'siz yol
   kullanılır: MonoBehaviour için assembly şeması, engine class için üretilmiş
   okuyucular. Ayrı TypeTree arşivlerinin (upstream notuna göre her düğümü bir TypeTree
   olan bundle'lar) okunması yapılmadı; bunun için format kanıtı yok.
6. **Raporlama.**
   - `SerializedFile.ExtractedTypeTreeCount`.
   - `SerializedAssetCollection.SerializedFormat`, `HasTypeTree`, `SerializedTypeCount`
     ve `ExtractedTypeTreeCount`.
   - Import sırasında uyarı: "N of M types in 'X' store their type trees outside the file".
   - `collection-export-report.json` içinde `ExtractedTypeTrees` listesi. Başarısızlık
     sayılmaz; şema kaynağının değiştiğini gösterir.
   - Ham kurtarma kaydında `RawCollectionInfo.ExtractedTypeTrees`.
   - TypeTree döküm aracı, extracted tipi "0 düğüm" yerine dışarıda tutulduğu
     bilgisiyle gösterir. Hash'in okunan dört `uint32` değeri hem konsola hem döküm
     dosyasına yazılır; kanıtlanmamış bir kanonik hash metni üretilmez.

## Engine şemaları

Engine class düzenleri `AssetRipper.SourceGenerated` NuGet paketinden gelir. Pinli
1.4.0 sürümünde sürüme bağlı sınıf adları şöyle:

- 6000.5: 33
- 6000.6: 59
- 6000.7: 11

Issue'daki "Read 916 but expected 920" (PlayerSettings, ClassID 129) farklı bir paket
sürümüyle görüldü. Bu pinde gerçek bir dosyayla doğrulanmadı. Paket değişikliği
gerekirse roadmap'teki gibi ayrı iş olarak izlenir.

## Doğrulanmamış varsayımlar

- Kayıtlı TypeTree boyutunun neyi kapsadığı ve hash'in girdisi.
- `"tthm"` sabitinin dosyadaki byte sırası (değer upstream'den).
- `SerializeReference` tiplerinin (`RefTypes`) aynı öneki taşıdığı.
- Header'da ve metadata'nın diğer bölümlerinde başka değişiklik olmadığı.

Hepsi upstream'in kendi deyişiyle ön bulgularına dayanıyor; gerçek bir 6000.5 dosyası
bunları doğrulayabilir ya da düzeltebilir.

## Phase sonu doğrulaması — 20 Ekim 2026

Yeni kategori: `SerializedFile23`. **28 IO + 17 import/corpus/export testi = 45**.
Corpus değişkenleri boşken **39 geçti / 6 NotExecuted**; corpus gerektiren vakalar
uydurma bir veriyle başarıya çevrilmiyor. Corpus etkin P12 testleri Debug ve Release
koşularında **45/45** geçti.

- **Bağımsız bayt fixture'ı:** Üretim `SerializedFile`/`TypeTree` yazıcısını kullanmadan
  kurulan little/big endian dosyalar. Normal tip ve `RefTypes` için satır içi/dışarıda
  ağaçların sekiz kombinasyonu; hash baytları, 64 bit PathID, nesne payload'ı,
  sıfırdan farklı dependency sayısı ve reference type adları okunup tekrar yazılıyor.
  TypeTree kapalı dosyada yeni önekler tüketilmiyor.
- **Olumsuz girdiler:** Yanlış magic, uyuşmayan sürüm ve negatif boyut; sürüm 24/64;
  tanınmayan eski veya sınır dışı sürümler; kısa v22/v23/gelecek header'ları; rastgele
  veriler ve dosya uzunluğu tutmayan gelecekteki header. Probe konumu korunuyor.
  Kaydedilmiş boyut ile gözlenen inline boyutun eşitliğinin henüz zorlanmadığı da açıkça sınanıyor.
- **Gerçek import/export yolu:** Dış TypeTree'li TextAsset üretilmiş engine okuyucusuyla
  açılıyor. Unicode payload ham kurtarma kaydında aynen kalıyor. Collection, journal,
  import uyarısı ve iki export raporunda **2/2** sayaç doğrulanıyor; bu bilgi başarıyı
  bozmuyor. Dört gerçek parser hatası iki exporter'da `ReadFile`/`ImportFailure` oluyor;
  bağımsız sağlam asset diske yazılmaya devam ediyor.
- **v22'den türetilen sentetik v23:** Hash denetimli Unity **6000.4.0f1** corpus'larının
  metadata'sı yeniden yazıldı; sürüm imzaları ve nesne payload'ları değiştirilmedi.
  Scalar alanlar ve PPtr hedefleri, SerializeReference polimorfizmi/alias/cycle grafiği,
  prefab hiyerarşisi ve **25 PPtr bağı**, hem inline hem MonoBehaviour/RefTypes dışarıda
  modunda mevcut bağımsız oracle'larla eşleşti. İki exporter'ın C# ve metin/binary
  TextAsset dosyalarının yol/hash listeleri v22 sonuçlarıyla birebir eşleşti.
- **TypeTree aracı:** Sentetik dış ağaçlı dosyayla gerçek CLI çalıştırıldı; regular/ref
  tip bilgisi ve dört hash word'ü konsolda ve `.txt` dökümünde doğrulandı.

Testlerin yakalayıp düzelttiği hatalar:

1. Kısa extended header probe'u dosya sonunu aşabiliyordu. v22 ve üstünde 48 bayt
   kontrolü eklendi; false dönerken akış konumu korunuyor.
2. Generic `WriteArray(int[])`, type-dependency elemanının Int32 genişliğini korumuyordu:
   tek `0` girdisi dört yerine bir bayt yazılıyordu. Metadata kayıyor ve nesne offset'i
   yanlış okunuyordu. Sayaç ve elemanlar
   açıkça endian-aware Int32 yazılıyor; v22 için iki endian regresyonu da var.
3. Farklı dış TypeTree hash'lerine sahip tipler eşit sayılıyordu. Regular/reference
   tiplerin eşitlik ve hash hesabına dış ağaç kimliği dahil edildi.
4. Döküm aracı `Hash128.ToString()` üzerinden değer yerine C# tip adını yazıyordu;
   yalnız dış ağaç içeren dosyada metin dökümü de oluşmuyordu. Değerler açıkça yazılıyor
   ve dış ağaç bilgisi döküm dosyasına da ekleniyor.

Tam solution regresyonu (8 test projesi), bütün mevcut corpus'lar ve kurulu Unity
6000.4 Editor etkin halde:

| Koşu | Sonuç |
|---|---|
| Debug | **960/960**, build 0 uyarı / 0 hata |
| Release | **959/959**, build 0 hata / 2 mevcut TODO uyarısı |
| P12, iki yapılandırmada da | **45/45** |
| P12, corpus değişkenleri boş | **39 geçti / 6 NotExecuted** |

Release'te yalnız önceden bilinen
`ExportIdHandlerTests.GetMainExportID_ValueGreaterThan100000_DebugAssertFails`
dışarıda bırakıldı. Debug/Release vaka kümelerinin farkı yalnız bu tek test.
Uyarılar mevcut SpriteConverter `instance.Edges` ve Animator TODO satırlarından.
Native Editor retry gerekmedi; SDK/paket yükseltilmedi ve Unity 6000.5 kurulmadı.

İki yapılandırmada ayrı ayrı **24 kanıt grubu / 38 binary dosya SHA-256'sı**
kontrol edildi; **76 nesne hash'i** kaydedildi. Her yapılandırmadaki dört v22–v23
script/metin export çifti eşleşti. TRX, kaynak hash'leri, artifact denetimleri ve CLI
dökümü [yerel phase kanıtında](../Source/0Bins/TestResults/P12-20261020/phase-verification.json).

Kanıtlar `Source/0Bins/Other/{AssetRipper.IO.Files.Tests,AssetRipper.Tests}/{Debug,Release}/TestResults/serialized-file23/`
altında: sentetik girdiler, tekrar yazılmış dosyalar, SHA-256 listeleri, export
raporları ve ham kurtarma journal'ı. Binary/corpus/kanıtlar git'e eklenmedi. CI artifact
kapsamına test kanıtı yolları eklendi; uzak CI çalıştırılmadı.

Hedefli tekrar çalıştırma (corpus ortam değişkenleri önce mevcut fixture dizinlerine atanmalı):

```sh
dotnet test --no-restore -p:AllowMissingPrunePackageData=true --filter TestCategory=SerializedFile23 --logger trx
```

## Gerçek format kabul kapısı

Sentetik phase doğrulaması, gerçek Unity 6000.5 format kabul kapısını kapatmaz.
Asıl kabul için gerekenler:

- Kaynağı ve hash'leri belli, gerçek Unity 6000.5 tarafından yazılmış v23 fixture.
- Inline/dış ağaç, `RefTypes`, marker byte sırası, boyut kapsamı ve header düzeninin
  gerçek dosyalar üzerinden denetlenmesi; hash doğrulaması için hash girdisinin belirlenmesi.
- Gerçek PlayerSettings dahil engine varlıklarının pinli şemalarla okunması ve
  çıkan projenin uygun Unity Editor'de doğrulanması.
- Ayrı TypeTree arşivlerini okuma, bu phase'de uygulanmadı; format kanıtıyla ayrı iş.
