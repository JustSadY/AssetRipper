# İlk decompile geliştirme paketi — uygulama ve doğrulama

Tarih: 19 Ekim 2026. Başlangıç commit'i: `beeb50ee1c223e856c860caa338e5309404956a3`.
Bu sonuçlar çalışma dizinindeki değişiklikleri kapsar; yeni bir commit veya PR oluşturulmadı.

## Uygulanan kapsam

- **P01'in ilk dilimi:** SystemTester, sıralı corpus çalıştırma, JSON raporu ve anlamlı çıkış kodları üretiyor. Klavye girdisi beklemiyor. Rapor; assembly/runtime sürümü, giriş ve gerçek çıktı yolları, yüklenen/işlenen nesne sayıları, assembly sayısı, süre ve hata/uyarı sayaçlarını içeriyor.
- **P02:** Unity-project ve primary-content script export aynı decompile sonuç sözleşmesini kullanıyor. Kullanılan ILSpy paketinin exception atmadan doldurabildiği `Errors` listesi de kontrol ediliyor. Kaydedilen hatalar JSON raporuna ve collection/export sonucuna taşınıyor.
- **P02 / placeholder:** Assembly hiç yüklenemediğinde veya yüklenmiş assembly'de istenen script tipi bulunamadığında üretilen dummy dosyalar ayrı listeleniyor. Bir başka assembly'nin işlenmesi, kurtarılabilir decompile hatasından sonra devam ediyor. İptal, bellek ve dosya sistemi hataları bu kategoriye sokulmuyor.
- **P03:** GUI, headless ve SystemTester mevcut export dizinlerini silmiyor. Kullanılmış hedefte benzersiz alt klasör seçiliyor. Boş bir dizin `.assetripper-export` dosyasıyla ayrılarak eşzamanlı/tekrarlı export'un aynı hedefi kullanması önleniyor. Web arayüzündeki silme uyarısı da yeni davranışa göre değiştirildi.

`script-export-report.json`, Unity export'ta `ExportedProject` içinde; primary-content export'ta çıktı kökünde yazılır. Başarılı decompile sonucu, C# derleme veya özgün davranış doğrulaması anlamına gelmez. Özellikle IL2CPP Level2 metot stub'ları bu paketle gerçek native gövdelere dönüşmedi.

## Doğrulama sonuçları

| Kontrol | Sonuç |
|---|---|
| Temiz başlangıç checkout'u, `AssetRipper.Tests`, Debug | **173 / 173 geçti** |
| Temiz başlangıç checkout'u, aynı proje, Release | **172 geçti, 1 mevcut hata** |
| Son çalışma dizini, tüm solution testleri, Debug | **546 / 546 geçti**, 8 test projesi |
| Yeni testler, Release | **17 / 17 geçti**: 13 decompile/dizin testi, 4 GUI/headless testi |
| Son solution Release build | Başarılı |
| SystemTester geçerli serialized fixture | Exit `0`; gerçek TextAsset baytları çıktı dosyasında doğrulandı |
| Aynı hedefe ikinci SystemTester export'u | Yeni alt klasör; mevcut dosyanın içeriği korundu |
| Eksik corpus / eksik girdi | Exit `1`; JSON başarısızlık raporu |
| Hatalı CLI seçenekleri | Exit `2` |
| Değişiklik ve localization kontrolleri | `git diff --check`, İngilizce/Türkçe JSON kontrolleri geçti |

Başlangıç Release hatası:
[`GetMainExportID_ValueGreaterThan100000_DebugAssertFails`](../Source/AssetRipper.Tests/ExportIdHandlerTests.cs).
Bu test, Release'de bulunmayan Debug assert davranışını bekliyor. Temiz checkout'ta da başarısız olduğu doğrulandı; bu paket kapsamında değiştirilmedi. Son Release doğrulaması yeni 17 testi kapsar; tüm Release testlerinin geçtiği iddia edilmiyor.

Yeni regresyonlar şunları kapsıyor:

- Kaynağı depoda bulunan managed örneğin C# üretimi.
- ILSpy'nin exception atmadan raporladığı hatalar ve sonraki assembly'ye devam.
- Script başarısızlığının Unity export sonucuna taşınması.
- Eksik assembly ve eksik tip için görünür placeholder üretimi.
- İptal ve dosya sistemi hatalarının normal decompile hatası olarak yutulmaması.
- Mevcut dosyanın korunması, dizin rezervasyonu, eşzamanlı export, root ve dosya hedeflerinin reddi.
- GUI/headless × Unity/primary-content birleşimlerinin dördünde mevcut dosyanın korunması; gerçek serialized girdiyle çalışma.

## Yeniden çalıştırma

Repo kökünden, uygun .NET 10 SDK/runtime kurulumu ile:

```bash
dotnet build AssetRipper.slnx -c Release
dotnet test AssetRipper.slnx --logger trx --results-directory /tmp/assetripper-results

dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj -c Release --filter 'FullyQualifiedName~ScriptDecompilationTests|FullyQualifiedName~ExportDirectoryTests'
dotnet test Source/AssetRipper.GUI.Web.Tests/AssetRipper.GUI.Web.Tests.csproj -c Release --filter 'FullyQualifiedName~GameFileLoaderTests'
```

Bu makinede sistem SDK'sında ASP.NET prune verisi/runtime eksikti. Restore için geçici `-p:AllowMissingPrunePackageData=true` kullanıldı; GUI testleri resmi ASP.NET 10.0.13 runtime'ının geçici kurulumuyla çalıştırıldı. Kesilen `SourceGenerator.Foundations.2.0.17` paketi, NuGet metadata'sındaki SHA-512 ile doğrulanarak geçici yerel kaynaktan tamamlandı. Repo SDK veya bağımlılık sürümleri değiştirilmedi.

SystemTester komutları ve rapor anlamları [araç README'sinde](../Source/AssetRipper.Tools.SystemTester/README.md) bulunuyor.

## Açık kalan işler

- Unity Editor'den üretilmiş, tam sürümü/backend/platformu kayıtlı Mono ve IL2CPP golden corpus henüz eklenmedi. Mevcut örnekler kendi C# kaynağımızdan ve serialized fixture builder'dan üretiliyor; Editor export'u gibi sunulmuyor.
- Native IL2CPP metot kurtarma, davranış karşılaştırması, Unity Editor import ve bellek/throughput benchmark'ı çalıştırılmadı.
- İlk pakette raw object journal ve kısmi MonoBehaviour payload koruması yoktu; sonraki [P05 ilk dilimi](RAW_RECOVERY.tr.md) bunları isteğe bağlı modda ekliyor. [P04 ilk dilimi](COLLECTION_ISOLATION.tr.md) import/export koleksiyon hata izolasyonunu, [P06 ilk dilimi](ASSEMBLY_IDENTITY.tr.md) assembly adayı/kimlik korumasını ekledi. Global processor hata sınırları ve tam shared-context izolasyonu açık.
- Yüklenen nesne sayaçları enjekte edilmiş engine kaynaklarını da içerebilir; henüz object-table envanter kapsamı metriği değildir.
