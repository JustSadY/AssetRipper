# Assembly kimliği ve aday koruması — P06 ilk dilim

Bu paket aynı dosya/basit isimli managed assembly adaylarının keşifte elenmesini, manager'da birbirini ezmesini ve stream/ILSpy cache'inde karışmasını önler. ILSpy referansları name/version/culture/public-key-token üzerinden çözülür. Belirsiz adaylar ve kontrollü sürüm fallback'i raporlanır. Yeni paket veya decompiler sürümüne geçilmedi.

## Adayların korunması

- `PlatformGameStructure.AssemblyCandidates`, farklı dizinlerde bulunan aynı adlı DLL yollarını da taşır. Yollar tam yola çevrilip aynı yolun tekrarları elenir; yükleme sırası ordinal yol sırasıdır. Legacy `Assemblies` sözlüğü ilk yolu göstermeye devam eder, kendi loader'ımız bütün adayları kullanır. `RequestAssembly`, birden çok yol varsa tek yol seçmez.
- Mixed input'ta doğrudan verilen DLL dosyası da assembly keşfine katılır. `.DLL` uzantısı dahil büyük/küçük harf farkı desteklenir. Mevcut platform tarama kapsamı bu dilimde genel bir recursive managed dizin taramasına dönüştürülmedi.
- `BaseManager`, fiziksel assembly nesnelerini ayrı tutar. Metadata basit ad indeksi ile dosya adı alias'ı ayrıdır. Yeniden adlandırılmış dosya hem alias hem gerçek adla bulunabilir; enumeration'da iki kez görünmez. Kaçırılmış bir arama, daha sonra yüklenen assembly'yi kalıcı olarak gizlemez.
- `Read` sırasında bütün PE akışının SHA-256 değeri ve verilen kaynak yolu kaydedilir. Normal `Load`, tam dosya yolu kaydeder; bundle resource'larında kaynak yolu mantıksal entry yoludur. Program içinde oluşturulmuş/sentetik assembly'lerde kaynak hash'i/yolu olmayabilir.
- Stream cache ve ILSpy PE cache'i assembly **nesne kimliğini** kullanır. Aynı CLR kimliğinde, hatta aynı MVID'de farklı baytlar içeren iki aday birleştirilmez. Orijinal okuma akışları lazy metadata için manager kapanana kadar tutulur; üretilmiş stream cache'i temizlenebilir.
- Eksik assembly istenince resource adayları tek tek okunur; aynı isimli resource nesneleri yalnız ad/yol eşitliğiyle elenmez. Bu, bütün bundle içi DLL'lerin önceden keşfedildiği anlamına gelmez. Önceden yüklenmiş bir ad için henüz istenmemiş resource adaylarının envanteri açık kalır.

`IAssemblyManager.GetAssemblyCandidates` ve `GetSourceInfo` yeni sorgu noktalarıdır. Varsayılan interface uygulamaları mevcut özel manager'ların derlenebilmesini korur. `BaseManager`'ın eski protected `m_assemblies` sözlüğünü doğrudan kullanan harici türevler yeni sorgu/API'lere uyarlanmalıdır.

## ILSpy çözümleme politikası

Kimlikte ad/culture/token normalize edilir; version korunur. Fiziksel aday seçimi:

1. Dört bileşende tam eşleşen tek aday varsa onu kullan.
2. Aynı kimlikte birden çok aday varsa `AmbiguousIdentity` kaydet ve seçim yapma.
3. Tam eşleşme yoksa aynı ad/culture/token'a sahip **tek** aday için `UniqueVersionFallback` uyarısıyla farklı sürüme izin ver.
4. Birden çok uyumlu sürümde `AmbiguousVersion`, yanlış culture/token'da `IdentityMismatch` kaydet; seçim yapma.
5. Hiç aday yoksa `Missing` kaydet. Eksik sonuç kalıcı PE cache girdisi yapılmaz.

`Resolve(AssemblyDefinition)` doğrudan verilen fiziksel adayı açar. Referansla aynı kimlikteki iki aday arasında seçim yapılamaması, her birinin ayrı decompile edilmesini engellemez. Module çözümlemesinde parent PE sahibi dikkate alınır; başka bir assembly'nin aynı adlı modülü rastgele dönmez. Ayrı netmodule payload'larını assembly manifest stream'i gibi sunmak yerine `UnsupportedNetModule` raporlanır.

`Missing` ve `UniqueVersionFallback` uyarıdır; tek başlarına C# üretimini başarısız saydırmazlar. Bu, referansların hedef Unity profile'ında doğrulandığı anlamına gelmez. Belirsizlik/identity mismatch/modül hataları script raporunun başarısını düşürür. Retargeting/content-type politikaları, ayrı netmodule loader'ı ve type-forwarder oracle corpus'u bu dilimde tamamlanmadı.

## AsmResolver context sınırı

Yerel pinli **AsmResolver.DotNet 6.0.1**, shared `RuntimeContext` içinde aynı basit adlı ikinci assembly'yi kabul etmiyor. Yerel deneyde `Same v1` kayıtlıyken `Same v2` isteği de `v1` döndü. Bu pakette AsmResolver'ın iç resolver/cache davranışı yeniden yazılmadı.

Bu nedenle yüklenmiş adaylarda basit ad çakışması varsa:

- İkinci aday korunur; shared context'e zorla eklenmez.
- Ana `ExportHandler.Process` akışında `IAssemblyProcessor` işaretli bütün script processor'ları o iş için atlanır. Core assembly processor'ları bu arayüzle işaretlendi. Asset processor'ları çalışmaya devam eder.
- Assembly tabanlı MonoBehaviour alan recovery'si başarısızlık nedeni döndürür. Serialized TypeTree'den okuma bu sınırlamaya bağlı değildir. Raw mod açıksa özgün nesne baytları önceki paket sayesinde korunur.
- `SharedContextRecoverySkipped: true` ve genel export başarısızlığı raporlanır.

Bu politika muhafazakârdır: byte olarak aynı DLL'nin farklı fiziksel kopyaları da şimdilik aday birleştirmesi yapılmadan çakışma sayılır. Çakışma yokken kullanılan AsmResolver shared context'in sürüm bağlama davranışı bu değişiklikle tam kimlik çözümleyicisine dönüşmüş değildir. Özel processor'lar işaretli arayüzü kullanmalı; doğrudan processor çağrıları veya daha önce yapılmış mutasyonlar pipeline guard'ıyla geri alınmaz. Geç gelen adaylara kadar tamamlanmış işlemlerin rollback'i yoktur.

Sonraki context işi, context bağımlı recovery'yi izole edip doğru referans profiliyle doğrulamaktır. İlk dilim, yanlış sürümle alan veya metot onarımı yapıp bunu başarı saydırmaz.

## Çıktılar ve raporlar

`AssemblyExportLayout`, çakışmasız adlarda mevcut dosya/klasör adını korur. Çakışan adlarda kimlik + MVID + orijinal içerik hash'inden türetilen ek kullanılır; hash yoksa mevcut PE stream'inin hash'i alınır. Adlar dosya sistemi için temizlenir. Üretilen adlar da başka normal/üretilen adlarla çakışmaya karşı ayrılır. CLR assembly adı değiştirilmez.

- **Unity-project:** aynı adı paylaşan adaylar `Assets/Plugins` veya tek script klasörüne yazılmaz. Bütün aday DLL'ler `AuxiliaryFiles/GameAssemblies` altında ayrı dosyalara kaydedilir. Sürüm belirtmeyen MonoScript bağları için raporlanmış placeholder üretilir.
- **Primary-content:** DLL'ler `Assemblies` altında, decompile kaynakları `Scripts/<ayrılmış-ad>` altında tutulur. `.slnx` farklı proje yollarını kullanır. MonoScript asset'i yoksa da yüklenmiş DLL'ler arşivlenir; bu durumda C# decompile koleksiyonu tetiklenmez.

`assembly-resolution-report.json` export kökünde yazılır. `SchemaVersion: 1` kaydı yüklenmiş managed adayları, kaynak hash/yollarını, MVID'leri, manager kimlik diagnostic'lerini, shared-context kararını ve ayrılmış DLL çıktı yollarını içerir. SystemTester aynı kaydı vaka JSON'unun `AssemblyResolution` alanına ekler. ILSpy sorgularının diagnostic'leri ayrıca `script-export-report.json` içindeki `ResolutionDiagnostics` alanındadır; tekil decompile sonuçları `Identity` taşır.

Çakışmalı export sağlam asset'leri çıkarsa bile exit `1` verir. `collection-export-report.json` yalnız koleksiyon döngüsünü anlatır; genel karar `LastExportSucceeded` / SystemTester vaka sonucudur. `OriginalSha256`, **okunan kaynak DLL'nin** hash'idir. Normal işlerde script processor'ları çıktıyı değiştirebilir; bu hash'i yeniden üretilmiş DLL'nin checksum'u olarak yorumlamayın. Tam özgün DLL vault'u bu dilimin sözleşmesi değildir.

Ayrılmış C# proje yolları, bütün referansların bu yollara göre derlenip doğrulandığı anlamına gelmez. Unity/MonoScript binding, C# compile ve davranış doğrulaması P07/P13 işlerinde kalır. [P07'nin ilk dilimi](MANAGED_ROUNDTRIP.tr.md), ayrı bir kaynak sahibi olduğumuz .NET fixture'ını derler ve davranışını sınar; çakışmalı DLL export'larının compile doğrulaması değildir. Başarısız bir assembly initialization'ın tamamını izole etmek ve okunamayan tüm DLL'ler için kalıcı keşif envanteri oluşturmak da bu dilimde tamamlanmadı.

P07 testlerinde ILSpy'ın iki token içermeyen sentetik framework sorgusu için yanlış `IdentityMismatch` saptandı. Bunlar tam eşleşme bulunmazsa `ImplicitFrameworkProbeUnresolved` uyarısı olarak kalır; gerçek PE referanslarında publisher/culture kontrolü korunur. Ayrıntı ve sınırlar [P07 notlarındadır](MANAGED_ROUNDTRIP.tr.md).

## Doğrulama

| Kontrol | Sonuç |
|---|---|
| Tüm solution, Debug | **608/608 geçti**, 8 test projesi |
| İlgili Release testleri | **79/79 geçti**: 18 kimlik testi + önceki 61 ilgili test |
| Solution Release build | Başarılı; mevcut animator TODO uyarısı dışında yeni uyarı yok |
| Gerçek DLL'lerle CLI, iki `Twin.dll` sürümü | İki aday ayrı saklandı; kaynak hash'leri ve arşiv baytları eşleşti; sağlam TextAsset çıktı; exit `1` |
| Aynı CLI girdisinin tek sürümlü karşılaştırması | Context guard devreye girmedi; geçerli çıktı ve exit `0` |
| Mevcut çıktı dosyası | Korundu |

`AssemblyIdentityTests`, keşif/alias/miss cache, version/culture/token eşleştirmesi, aynı kimlik/MVID'de farklı içerik, stream/PE cache ayrımı, modül sahibi, sıra bağımsız çıktı adları, iki export yolu, MonoScript olmadan DLL koruması ve çakışmada metot stubbing'in atlanmasını kapsar. Fixture'lar kendi kaynağımızdan üretilir; Unity Editor golden corpus değildir.

Testler, ILSpy'nin paralel dosya açmalarında VirtualFileSystem dictionary yarışını da açığa çıkardı. `ILSpyWholeProjectDecompiler`, dizin/dosya açma işlemlerini kilitler; ayrı dosyalara içerik yazımı paralel kalabilir. Throughput veya bellek kazancı ölçülmedi.

Tam Release suite'in geçtiği iddia edilmiyor; mevcut Debug-assert test sınırı [baseline notlarında](DECOMPILATION_BASELINE.tr.md) açıklanır. Tekrar çalıştırma:

```bash
dotnet build AssetRipper.slnx -c Release
dotnet test AssetRipper.slnx --logger trx --results-directory /tmp/assetripper-results
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj -c Release --filter 'FullyQualifiedName~AssemblyIdentityTests'
```
