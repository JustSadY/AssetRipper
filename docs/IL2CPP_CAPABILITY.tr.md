# IL2CPP sağlayıcı yetenek deneyi — P10 / F4a

## Sonuç ve karar

Unity **6000.4.0f1 / Linux64 / x86_64 / metadata 39** üzerinde kaynak sahibi
olduğumuz aynı projenin Mono ve IL2CPP oyuncuları 20 bağımsız davranış kontrolünü
geçti. Pinli Cpp2IL'in varsayılan çıktısı minimal stub üretiyor; Level3 seçimi tek
başına metot kurtarma sağlamıyor. Deneysel IL üretiminde somut bir ilerleme yolu
var, fakat sağlayıcı genel kullanıma hazır kabul edilmedi.

| Aşama | Eşleşen gözlem | Anlamı |
|---|---:|---|
| Kaynak Editor oracle'ı → Mono oyuncu | 20/20 | Kaynak davranışı |
| Aynı kaynak → IL2CPP oyuncu | 20/20 | Native build oracle'ı |
| Mono DLL → ayrı .NET 10 replay süreci | 20/20 | Ölçüm düzeneği kontrolü |
| Varsayılan Cpp2IL çıktısı | 3/20 | Stub negatif kontrolü; sıfır sonuçlarının tesadüfen eşleşmesi kurtarma değildir |
| Deneysel IL üretimi, değiştirilmemiş çıktı | 0/20 | Altı metotta `MaxStack=0`; `InvalidProgramException` |
| Ayrı çıktıda stack yüksekliği yeniden hesaplandı | 10/20 | Kısmi davranış; genel doğruluk kanıtı değildir |

Son aşamada `Counter.Step` için üç alan güncellemesi ve `Echo<int>` için tek
gözlem geçti. `Affine` 1/5, `Branch` 3/4, `Sum` 2/4, `Divide` 0/3 eşleşti.
Üretilen IL'de çözülemeyen bellek/çağrı/instruction yer tutucuları bulunuyor.
Stack yüksekliği hesabı başarılı olsa bile yanlış değerler ve exception hatası
sürüyor; tür doğruluğunu veya exception-region rekonstrüksiyonunu kanıtlamıyor.

**Karar:** P11'de önce izole araştırma worker'ı, eksik x86 operand/LEA ve
typed-CIL doğrulaması geliştirilir. Deneysel sağlayıcı ürünün varsayılanı yapılmaz.
`RecoveredNative` veya üretimde `BehaviorPassed` etiketi bu paketle eklenmedi.

P11'in ilk dilimi olan opsiyonel süreç izolasyonu ve yapısal aday kapısı
[izole IL2CPP worker'ı](IL2CPP_WORKER.tr.md) belgesinde. İkinci dilim yanlış
çift reddini, LEA dönüşümünü ve tipli CIL kapısını ekledi. Bu deneyin `Affine`,
`Branch`, `Sum` ve `Step` gövdeleri orada bütün kapılardan geçti ve 16/16 gözlem
eşleşti.

## Üretim raporu ve düzeltme

`IAssemblyManager.Il2CppRecovery` opsiyoneldir; mevcut implementasyonlar için
varsayılanı null'dır. `IL2CppManager.Initialize`, seçilen output biçimini ve
processing-layer listesini iş başında yakalar. Hook'ların callback içinde
değişmesi devam eden işin output seçimini değiştirmez. PreProcess'in gerçek
layer listesine ekledikleri rapora dahil edilir.

İki script export yolu ve placeholder script yolu, `script-export-report.json`
içine bu bilgiyi taşır:

- `RequestedContentLevel`, sağlayıcının tip/assembly/sürüm/ID bilgisi ve layer'lar.
- `UsesKnownStubOutput`: yalnız tam `AsmResolverDllOutputFormatDefault` tipi için
  true. Custom alt sınıfın aynı ID'yi kullanması bilinen stub sözleşmesi sayılmaz.
- `MethodBodies.GeneratedStub`, `NoBodyByDesign`, `Unknown`: abstract, extern,
  runtime/native/internal-call bildirimleri gövde kurtarma başarısızlığı sayılmaz.
  Custom sağlayıcıdaki dolu CIL bile doğrulanmadan `Unknown` kalır.
- `Validation=NotRun`: C# dosyasının yazılması native davranış kontrolü değildir.
  Raporun mevcut `Succeeded` alanı C# export anlamını korur.

Bu gerçek native corpus'ta varsayılan üretim hattı **16.000 stub / 827 gövdesiz
bildirim / 0 unknown** saydı. Bunlar tüm build assembly'lerine aittir;
oyun kodunda kurtarılmış metot sayısı değildir.

Level3'te varsayılan stub seçildiğinde null-by-reference gövde düzeltmesi artık
uygulanır. Custom/unknown Level3 gövdeleri ve dolu Mono Level2/3 gövdeleri
korunur. Önceden Level3'ün mutlaka recovery olduğu varsayımı bu düzeltmeyi
atlıyordu. SDK veya paket sürümü değiştirilmedi.

## Pin ve REA kanıtı

Kurulu `AssetRipper.Cpp2IL.Core` **1.0.9**, assembly **1.0.9.0**, MVID
`487b796a-50f9-4dfc-9cfc-43355caf72ba`:

```text
SHA-256 be5f122b54dba1f02b46411393fd12db47138d42dd512b8aeae414e26cc6eb78
repository commit 291ce6cb95e9cbe661831c53d5ed8fff833d8318
```

Paket nuspec'i bu commit'e işaret ediyor. Tam pinli
[varsayılan output](https://github.com/SamboyCoding/Cpp2IL/blob/291ce6cb95e9cbe661831c53d5ed8fff833d8318/Cpp2IL.Core/OutputFormats/AsmResolverDllOutputFormatDefault.cs)
minimal gövde çağırıyor.
[Deneysel recovery output](https://github.com/SamboyCoding/Cpp2IL/blob/291ce6cb95e9cbe661831c53d5ed8fff833d8318/Cpp2IL.Core/OutputFormats/AsmResolverDllOutputFormatIlRecovery.cs)
boş ISIL durumunu da başarı sayacına ekliyor.
[IL üreteci](https://github.com/SamboyCoding/Cpp2IL/blob/291ce6cb95e9cbe661831c53d5ed8fff833d8318/Cpp2IL.Core/IlGenerator.cs)
max-stack hesabını kapatıyor ve bazı çözülemeyen işlemleri `Console.WriteLine`
yer tutucularına dönüştürüyor. Bu yüzden sağlayıcı başarı sayacı kabul ölçüsü değil.

[REA skill'i](/home/lykia/.agents/skills/reverse-engineer-anything/SKILL.md)
ile DLL yüklenmeden kimlik, üyeler, çağrı kenarları ve ham CIL incelendi:

- Paket kimliği: `ev_94a892675bac3cfab3d17fd164e7f5ea21a5fb434d1ce08580f617337c60814c`.
- Recovery üyeleri: `ev_754cf5a453be7714f149965a061dd8ea94e353813d2a4de678750aa4629d9dd3`.
- Üreteç: `ev_e91fb1bd2a99cba171d670d05e5f9b199da98f8303fc2f66cf7423ad51a1c862`.
- Gerçek ham aday DLL: `ev_5400072e64bd0c596196d127e735ae28ca6735bd5604957348b296a95db165cf`.
  Seçili altı metot tam decode edildi, hepsinde fat header/max-stack 0 görüldü.
- Son kanıt kopyaları: ham aday `ev_d9cde055b6f9f0c74f382e275931b7a6987027b6f298bea31eb8d3a747798e21`,
  stack-rebuilt aday `ev_3ca910fe01f8786220122c88fe5b8a42796bd8954e8a06e10de78282f93e4798`.
  Altı metodun ham CIL hash'leri değişmedi; max-stack başlık değeri 1/2'ye geldi.
  `Divide` çıktısında exception region sayısı hâlâ sıfır. İki tam statik kayıt
  Evidence içinde saklandı.

Statik REA kaydı davranış kanıtı olarak kullanılmadı; o kanıt ayrı replay
sonuçlarıdır. Bağımsız upstream sürüm deneyi veya ARM64 karşılaştırması yapılmadı.

## Corpus, çalıştırma ve kanıt dosyaları

[Kaynak proje](../TestProjects/Il2CppMethods/README.md), ayrı geçici dizinde
iki Linux oyuncu üretir. Minimal stripping, Preserve/link.xml ve NoInlining
kullanılır. İlk deney doğrudan `a/b` için sıfıra bölmede native SIGFPE verdi:
üretilen C++ kodunda division check yoktu. Başarısız çıktı/log korundu. Yeni
corpus'ta açık `DivideByZeroException` fırlatılması kullanıldı; aynı 20 beklenen
değer hem Mono hem IL2CPP'de ayrıca doğrulandı. Bu değişiklik exception kurtarma
başarısı diye sayılmıyor.

Yerel Linux derleyicisi `libncurses.so.6` bulamadığında kurulu Unity Android NDK
içindeki aynı SONAME'li host kütüphanesi yalnız geçici bir destek dizinine
kopyalandı ve build sürecinin `LD_LIBRARY_PATH` değerine verildi. Sistem dosyası,
Unity kurulumu veya global konfigürasyon değiştirilmedi. Bu host workaround'u
üretici scriptine gömülmedi.

Doğrulanmış yerel corpus:
`Source/0Bins/TestCorpus/UnityIl2Cpp6000.4-20261020-e142ab68`, **409 hash girdisi**.

```text
GameAssembly.so: c58de5ab11f87a75653306a7da0e38edd1b9be6f2a672206dfac0aef995aefa0
global-metadata.dat: 8e7d64fe16c84b7e81a42f57c2d1df36180c6c48f610cc215dfd18b5947bce7e
Mono Assembly-CSharp.dll: 41f98ef7031d755fcbf78ea5e61df9547a6506c56220ad12571945038f852a12
```

```sh
python3 TestProjects/build_il2cpp_fixture.py \
  --unity /path/to/6000.4.0f1/Editor/Unity --output /absolute/new-empty/corpus
ASSETRIPPER_UNITY_IL2CPP_FIXTURE=/absolute/corpus \
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter 'TestCategory=Il2CppCapability|TestCategory=Il2CppNative' --logger trx
```

Yerel restore workaround'u gerekirse mevcut paketlerle `--no-restore
-p:AllowMissingPrunePackageData=true` kullanılabilir. `Il2CppCapability` içindeki
28 kontrol kurulumsuz çalışır; `Il2CppNative` tek gerçek deneyi corpus yoksa
açıkça atlar. Bozuk hash/oracle/backend/girdi veya eksik gözlem skip sayılmaz.

Her gerçek deneme `TestResults/il2cpp-capability/<job>/` altında manifest,
oracle, `capability.json`, değişmemiş/stack-rebuilt aday DLL'leri, replay kaynak
ve davranış sonuçlarını korur. Özgün IL2CPP token, imza, pointer, RVA, raw offset
ve aynı adresi paylaşan metot sayısı kaydedilir. Token yalnız kendi metadata
dosyası içinde kimliktir. Branch offset'leri serialization sonrası gerçek DLL'de
vardır; serialization öncesi instruction metni tek başına adres kanıtı sayılmaz.

Her managed replay 30 saniyeyle sınırlı ayrı süreçtedir; yalnız bu kaynak
corpus'u ve hash'i doğrulanmış aday çalışır. Native analiz test host'unda kaldığı
için P11'in tam worker/crash/reset izolasyonu tamamlanmış değildir. Bu deney
hâlâ test host'unda analiz yapar; ürün import'u için ayrı süreç yolu P11'de
[izole worker](IL2CPP_WORKER.tr.md) ile eklendi. Ürüne veya
SystemTester'a genel oyun kodu yürütme yolu eklenmedi. CI yalnız seçili JSON ve
Evidence dizinini alır; tam Editor log'ları/lisans bilgileri yüklenmez.

## Doğrulama kaydı

Corpus'lar etkin tam Debug **824/824**, yeni 29 kontrol Debug ve Release'de
**29/29** geçti. Release seti **823/823**: yalnız mevcut Debug.Assert bağımlı
`ExportIdHandlerTests.GetMainExportID_ValueGreaterThan100000_DebugAssertFails`
dışarıda bırakıldı. Son solution Release build **0 uyarı / 0 hata**; önceki
sağlayıcı rebuild'inde mevcut Animator TODO uyarısı görüldü. Corpus'suz koşu
**28 Passed / 1 NotExecuted** verdi. Editor otomatik retry'si gerekmedi.

Bağımsız denetim 409 girdi hash'i, Debug/Release'teki 14 kanıt hash'i, 12 ELF
pointer/raw-offset eşlemesi, altı Mono/native method token bağı ve aynı kalan
altı CIL gövde hash'ini doğruladı. `Echo` native adresi iki method context ile
paylaşılıyor; tekil generic mapping varsayılmadı. Denetim sonucu
`TestResults/il2cpp-capability/Audit-20261020/audit.json` içinde. Test TRX'leri
yerelde `/tmp/assetripper-il2cpp-full-debug-results`,
`/tmp/assetripper-il2cpp-full-release-results`, son native kanıt koşusu
`/tmp/assetripper-il2cpp-artifact-results` altında. Uzak CI çalıştırılmadı.

## Açık kabul kapıları

Binary/metadata yapısal çapraz eşleme ve yanlış çift negatif kontrolü, ayrı
provider süreçleri, generic sharing matrisi, typed-CIL ve exception bölgesi
doğrulaması, C# yeniden derleme/behavior zinciri, çağrı/interface/virtual dispatch
ve diğer Unity sürüm/platformları açık. `Counter.Step` doğrudan reflection ile
çağrıldı; sanal veya interface dispatch ayrıca kanıtlanmadı. Kaynakta Preserve
kullanıldığı için bu corpus stripped metot kurtarma testi değildir.
