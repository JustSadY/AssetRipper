# İzole IL2CPP worker'ı — P11

## Durum

| Dilim | Kapsam | Kanıt durumu |
|---|---|---|
| İlk dilim | Ayrı süreç, protokol, ürün bağlantısı, yapısal stack kapısı | Test edildi: hedefli Debug 73/73 |
| İkinci dilim | Yanlış binary/metadata çifti reddi, LEA dönüşümü, ISIL kapısı, tip çıkarımı, tipli CIL doğrulayıcısı, `RecoveredNative`, sidecar kanıtı, heap sınırı, girdi kopyası silme | Test edildi: kontrollü 69/69, gerçek corpus 8/8 |
| Üçüncü dilim (kapanış) | Referans hiyerarşisi kontrolü, virtual/interface dispatch replay'i, generic instantiation matrisi, stripped ve Android ARM64 corpus'ları, çapraz mimari çift, ARMv8 lifter ölçümü | Test edildi: IL2CPP kategorileri 116/116, tam paket aşağıdaki kayıtta |

İkinci dilimin kodu önce testsiz yazıldı; toplu test turu dört hata buldu ve
hepsi düzeltildi (bkz. [Test turunda bulunan hatalar](#test-turunda-bulunan-hatalar)).
Üçüncü dilimin bulguları [P11 kapanışı](#p11-kapanışı) bölümünde. Aşağıdaki sonuçlar
düzeltilmiş kodun ölçümüdür.

## Sonuç ve karar

IL2CPP native analizi isteğe bağlı olarak ana süreçten ayrılabiliyor.
`ImportSettings.Il2CppWorker` boşsa davranış değişmez: mevcut in-process
`IL2CppManager` kullanılır. Ayar verildiğinde `IsolatedIL2CppManager` her analiz
için yeni bir `AssetRipper.Tools.Il2CppWorker` süreci başlatır. Cpp2IL yalnız bu
çocuk süreçte çalışır; ana sürecin `Cpp2IlApi.CurrentAppContext` değeri gerçek
corpus testlerinde değişmedi.

Ürün bağlantısı yalnız mevcut metadata/stub çıktısını (`dll_default`) taşır.
Deneysel CIL adayları ayrı bir araştırma API'sidir; manager onları seçmez.
Bütün kapılardan geçen aday gövde, yalnız davranışı corpus'larda doğrulanmış
x86-64 lifter yolunda `RecoveredNative` olarak işaretlenir.
`BehaviorValidation` üründe her zaman `NotRun` kalır; davranış kanıtı yalnız
testlerdeki oracle replay'idir. Üründe `BehaviorPassed` etiketi yoktur.

Bu bir **çökme/durum izolasyonudur, işletim sistemi güvenlik sandbox'ı değildir.**
Worker, kullanıcının yetkileriyle çalışan güvenilir yerel bir araçtır. Heap sınırı
yalnız .NET GC'nin managed heap'ini kapsar; native bellek, CPU ve dosya sistemi
için kota yoktur.

### Gerçek corpus ölçümü

Unity **6000.4.0f1 / Linux64 / x86_64 / metadata 39** kaynak sahibi corpus'u:

| Senaryo | Sonuç |
|---|---|
| Metadata/stub, iki ardışık iş | 49 assembly, **16.000 stub / 827 gövdesiz / 0 unknown**, `RecoveryCapability=RecoveryUnavailable`; her iş farklı PID ve job dizininde |
| Çift doğrulaması, doğru çift | `Passed`: 49 image = 49 modül, 3400 tip, 16.827 metot, 14.848 sıfırdan farklı pointer |
| Normal `GameStructure.Load`, ana süreçte sürüm boş | Çocuk süreç `globalgamemanagers` kopyasından `6000.4.0f1` belirledi; oyun geçerli yüklendi |
| 64 sıfır bayttan oluşan native girdi | Çocukta `ExitedWithError`; sonraki iş başka PID'de `Completed` |
| Yapılandırılmış worker dosyası yok | `StartFailed`; oyun sessizce `Unknown` backend'e geçmedi |
| Deneysel aday seçimi (`PureMethods`, `Counter`) | **4 `RecoveredNative`**: `Step`, `Affine`, `Branch`, `Sum`; replay'de bu dört gövdenin **16/16** gözlemi native oracle ile eşleşti |
| Varyant binary + orijinal metadata (metadata'da 1 metot eksik) | `RejectedPair`, exit 3: "Module 'Assembly-CSharp.dll' has 17 method pointers; its metadata image declares 16 methods." |
| Orijinal binary + varyant metadata (metadata'da 1 metot fazla) | LibCpp2IL kendisi düşüyor: `LibCpp2ILInitializationException (IndexOutOfRangeException)`; `AnalysisFailed` |
| Varyant binary + varyant metadata | `Passed`; image/tip/modül sayıları orijinalle aynı, metot sayısı tam bir fazla |
| `Step` virtual ve interface üzerinden | `AbstractCounter.Step` ve `ICounter.Step` çağrıları kurtarılan override'a ulaştı: **6/6** |
| `Echo<T>` instantiation matrisi | `Echo<int>` (adresini 2 method context paylaşıyor) ve `Echo<__Il2CppFullySharedGenericType>` (ayrı gövde) |

Kurtarılmayan iki metot stub'da kaldı:

- `Divide`: çözülemeyen bir runtime helper çağrısı içeriyor (`Call 6D6A40`, `UnresolvedCallTarget`);
  `catch` bölgesi native C++ exception tablolarında (bkz. [P11 kapanışı](#p11-kapanışı)).
- `Echo<T>`: native gövdesini iki method context paylaşıyor (`SharedGenericBody`).

Bu yüzden replay toplamı 20 gözlemde `Failed`. Test bunu açıkça bekliyor;
stub'ların tesadüfi eşleşmeleri kurtarma sayılmıyor.

Karşılaştırma: P10'da pinli sağlayıcının ham deneysel çıktısı 0/20, ayrı stack
düzeltmesiyle 10/20 verdi; ilk P11 dilimi yer tutucu reddiyle 2 aday tuttu. Bu
dilimde kapılardan geçen her gövde, kendi gözlemlerinin tamamında doğru çıktı;
yanlış kurtarma iddiası yok. Bu sonuç bu corpus'un altı metoduna aittir, genel
recovery başarısı değildir.

## P11 kapanışı

İkinci dilimden sonra açık kalan maddeler ve sonuçları:

| Madde | Sonuç |
|---|---|
| Referans hiyerarşisi | Uygulandı. 6 birim vakası: türetilmiş → base ve interface kabul; ilgisiz tip, ters yön ve ilgisiz receiver red; bağlamsız eski davranış. Gerçek corpus'ta dört gövde aynen geçti; `Step`'in receiver ve alan sahibi kontrolleri dahil. |
| Virtual/interface dispatch | Replay, `Step` gözlemlerini `AbstractCounter.Step` ve `ICounter.Step` üzerinden de çalıştırıyor. Orijinal corpus **6/6**. Stripped corpus **3/3**: kimse interface üzerinden çağırmadığı için UnityLinker `ICounter`'ı silmiş; test yüzeyleri çıktı DLL'inden sayıyor. |
| Generic paylaşım matrisi | Her generic metot için instantiation listesi raporlanıyor. `Echo<T>`'nin iki instantiation'ı var: `Echo<int>` (adres 2 context'le paylaşılıyor) ve `Echo<__Il2CppFullySharedGenericType>` (ayrı paylaşılan gövde). Politika gereği stub kalıyor. |
| Stripped corpus | `--stripped`: `[Preserve]` yok, boş `link.xml`, High managed stripping. 46 image, 2722 tip, 12.708 metot; çift `Passed`. Aynı dört gövde `RecoveredNative`, **16/16**. |
| İkinci mimari | `--target android-arm64`: ARM64 IL2CPP APK, emulator'da arm64-v8a çeviriyle **20/20** oracle. Çift `Passed` (`ArmV8`, 18 image, 2347 tip, 12.354 metot); metadata-only import çalışıyor. Kurtarma reddedildi, gerekçesi aşağıda. |
| Çapraz mimari çift | x86 binary + ARM64 metadata ve tersi: LibCpp2IL registration yapılarını bulamıyor ("Failed to find code registration or metadata registration"), `AnalysisFailed`. Şekiller bu kadar farklıyken çift kontrolüne sıra gelmiyor; kontrolün işi varyant testindeki gibi neredeyse aynı çiftler. |
| `Divide` ve exception region | Native gövde runtime helper (`0x6D6A40`) ve C++ exception çalışma zamanının PLT girişlerini (`0xE39Axx`) çağırıyor; `catch` bölgesi ISIL'de değil, native exception tablolarında (LSDA). Bunun rekonstrüksiyonu roadmap'in araştırma kararı kapsamında, P11 dışında. Helper adlandırma kodu Cpp2IL'in bildiği adreslere ad veriyor, ama bu corpus'taki adresler o listede yok; olumlu yolu ölçülmedi. |
| P10 deneyinin worker'a taşınması | Taşınmadı. P10 testi, pinli sağlayıcının ham çıktısını ölçen tarihsel bir in-host deney olarak kalıyor. İzole yoldaki karşılığı ve daha sıkı hali worker aday denemesi. |
| Diğer Unity sürümleri | Yerelde engelli: kurulu 2022.2.16f1'de yalnız Linux Mono desteği var, IL2CPP modülü yok. |

### ARMv8 lifter ölçümü

Pinli eski ARM64 lifter ISIL üretmiyor; `GetIsilFromMethod` boş liste döndürüyor.
Aday işi bu yüzden sağlayıcının yeni `NewArmV8InstructionSet`'ine geçiyor.

1. **Aliasing olmadan:** `Affine` ve `Sum` bütün yapısal ve tipli kapılardan geçti,
   ama gövde `return x` oldu. Sebebi, lifter'ın `W0` ile `X0`'ı farklı yazmaç sayması:
   32-bit sonuç return yazmacına ulaşmıyor ve ölü kod olarak siliniyor.
2. **Aliasing ile:** `Il2CppArmV8RegisterAliasInstructionSet`, SSA'dan önce `Wn`
   adlarını `Xn`'ye çeviriyor; x86 lifter'ın `eax`→`rax` yaklaşımıyla aynı. Bu kez
   `Affine`, `Branch` ve `Sum` kapılardan geçti, ama replay **1/5, 2/4, 2/4** verdi:
   - `lsl w8, w0, #3` (`UBFM` takma adı) `ShiftRight 29; And 0x0FFFFFFF` olarak çevriliyor.
   - `Sum`'ın döngüsü `Return X31`'e iniyor.
   - `Step` `BIC` komutunda `NotImplemented`, `Divide` `MemoryStore` ile reddedildi.
3. **Karar:** ARMv8 yolunda `RecoveredNative` verilmiyor. Kapıları geçen gövde son
   kapı olarak `UntrustedLifter` alıyor ve stub kalıyor. Kapı sonuçları ve sidecar'lar
   kanıt olarak tutuluyor. Bu ölçüm, yapısal ve tipli kapıların davranış kanıtının
   yerine geçmediğini gerçek bir örnekle gösteriyor.

ARMv8 sidecar'larında sağlayıcının disassembler'ı `ArgumentOutOfRangeException`
verdi; ISIL ve CIL bölümleri tam.

Android çalıştırması builder'ın açıp kapattığı bir emulator örneğinde yapılır:
`smoke36`, android-36 google_apis x86_64, `libndk_translation`, `-gpu
swiftshader_indirect`. Android 16'nın tek seferlik tam ekran onayı odağı aldığında
Unity ilk sahneyi yüklemiyordu. Builder bu onayı önceden "görüldü" sayıyor, ekranı
uyandırıp kilidi açıyor ve açılış ekranını kapatıyor. Rapor logcat'e numaralı
parçalar halinde yazılıyor. APK'dan yalnız `lib/` ve `assets/` açılıyor:
`aapt2`'nin kısalttığı `res/` adları yalnız harf büyüklüğüyle ayrışabiliyor.

## Çift doğrulaması

`Il2CppNativePairValidator`, `Cpp2IlApi.InitializeLibCpp2Il` bittikten hemen sonra,
processing layer ve output çalışmadan önce koşar. Worker her işte açar
(`IL2CppManager.ValidateNativePair = true`). In-process import'ta varsayılanı
kapalıdır, böylece mevcut davranış değişmez.

Neden gerekli: LibCpp2IL registration yapılarını arama ile buluyor. Bir modülün
metot pointer tablosu okunamazsa tabloyu **sessizce sıfırla dolduruyor**. Bir
metodun pointer'ını da token'ın satır numarasıyla modül tablosundan alıyor.
Metadata, binary'den daha az metot bildirirse her token sınırlar içinde kalır;
LibCpp2IL hata vermez ve metotları **kaymış native adreslere** eşler. Varyant testi
bu yönü gerçek bir build ile gösterir.

Kontroller binary'nin kendi tablolarını metadata ile karşılaştırır:

- Binary'deki tip boyut tablosu girdi sayısı = metadata tip tanımı sayısı.
- Her metadata image'ı için aynı adlı bir codegen modülü var. Metadata 24.2–27
  aralığında modül sırası, image'ın `assemblyIndex` değerine eşit. 27 ve sonrasında
  LibCpp2IL modülü adla bulduğu için sıra kontrol edilmez.
- Modülün `methodPointerCount` değeri = o image'daki tiplerin metot sayıları toplamı.
  Bu eşitlik gerçek corpus'un 49 modülünde tuttu.
- Okunabilen pointer dizisi uzunluğu = bildirilen sayı.
- Image'lardaki metot toplamı = metadata metot tablosu.
- En az bir pointer sıfırdan farklı ve sıfırdan farklı her pointer binary içine eşleniyor.

Metadata 24.2'den eski çiftlerde codegen modülü yoktur; yalnız ilk kontrol yapılır
ve sonuç `Status=Partial` olur. Uyuşmazlıkta en fazla sekiz neden, sayılarıyla
birlikte `Il2CppPairMismatchException` içinde döner. Worker bunu `RejectedPair`
(exit 3) olarak raporlar. Sonuç `Il2CppRecovery.PairValidation` alanında taşınır.
Sayı eşleşmesi, aynı şekle sahip iki farklı build'i ayırt edemez.

### Recovery yeteneği

`Il2CppRecovery.RecoveryCapability` değerleri:

- `NotRequested`: Level1/2.
- `RecoveryUnavailable`: Level3 ve bilinen stub provider.
- `ExperimentalCandidates`: deneysel aday çıktısı.
- `UnverifiedCustomProvider`: diğer custom provider'lar.

Davranış iddiası değildir.

## Deneysel aday hattı

### LEA dönüşümü

Pinli x86 lifter, yığın dışı `lea r, [bellek]` komutunu bir bellek okumasıyla
**aynı** `Move r, [bellek]` biçiminde ISIL'e çeviriyor. Base ve displacement
olmayan biçimde (`lea eax,[rdi*8]`) index'i tamamen düşürüp `[]` üretiyor. IL
üreteci her ikisini de `Unmanaged memory load` yer tutucusuna dönüştürüyor.

`Il2CppAddressLoweringInstructionSet` pinli `X86InstructionSet`'in alt sınıfıdır:

- Sağlayıcının ISIL'ini üretir.
- Aynı native baytları Iced ile yeniden çözer ve `Move reg, [bellek]` üreten bütün
  komutları (mov ailesi, cmov, xchg, lea) sırayla hizalar. Bu hizalamada kayıplı
  biçimler de sayılır: RIP-relative, mutlak adres ve yalnız index.
- Hizalama birebir tutmazsa hiçbir şeyi değiştirmez (`AlignmentFailed`).
- Tutarsa yalnız LEA hareketlerini, SSA ve tip yayılımından önce, yazmaç
  seviyesinde `Add`/`Multiply` dizisine çevirir. Yazmaçlar Iced operand'ından
  kurulur, böylece düşürülen index de geri gelir. İlk işlem özgün instruction
  nesnesini kullanır; dal hedefleri bozulmaz.
- 4/8 baytlık hedef ve 64-bit genel amaçlı yazmaç adresleri desteklenir;
  RIP-relative LEA ve diğer biçimler değiştirilmez (`Partial`).

Yalnız deneysel aday işinde ve yeni worker sürecinde,
`ApplicationAnalysisContext.InstructionSet` alanına atanır. x86-64 dışındaki
mimarilerde seçili metotlar `UnsupportedArchitecture` alır.

### Tip çıkarımı

Pinli tip yayılımı bazı bayrak ve geçici değerleri tipsiz bırakıyor, IL üreteci de
bunları `object` local olarak bildirip içine int32 yazıyor (`Branch`'te `test al,1`
sonucu). `Il2CppLocalTypeInference` tipsiz bir local'i yalnız bütün tanımları
uyuşuyorsa tipler:

- Tek bir bilinen Int32/Int64 tipindeki operand'lar üzerinde tamsayı aritmetiği → o tip.
- Karşılaştırmalar, ve Boolean'ların `Not`/`Move`/`And`/`Or`/`Xor`'u → Boolean.

Çağrı sonuçları ve diğer tanımlar tipsiz kalır; son kararı tipli CIL kapısı verir.

### Kapı zinciri ve `RecoveredNative`

Seçili bir gövde yalnız şu sırayla geçerse tutulur:

1. **Ön koşullar.** Constructor ise `UnsupportedConstructor`, native pointer yoksa
   `NoNativeBody`. Metot veya tipi generic ise ya da aynı native adresi başka metot
   paylaşıyorsa `SharedGenericBody`: paylaşılan gövde tek bir özgün metoda eşlenmez.
2. **`IsilChecked`.** IL üretecinin yer tutucu bırakmadan anlamı değiştirdiği yollar
   reddedilir:
   - `[local]` adresine yazma local'in üzerine yazmaya, `[local]`'den okuma
     dereference'sız okumaya dönüşüyor.
   - Eksik çağrı argümanına varsayılan değer basılıyor.
   - Kayıp return değeri `ldnull`, çözülemeyen constructor `ldnull`, tip operand'ı
     sahte `newobj` oluyor.
   - Çözülemeyen çağrı hedefi, desteklenmeyen ISIL opcode'ları ve analiz uyarıları.
3. **Yer tutucu taraması.** Pinli üretecin 17 tanılama önekinin hepsi, gövde sonuna
   eklenen `Warning:` satırları dahil.
4. **`StackHeightChecked`.** Label ve stack yüksekliği.
5. **`TypedCilChecked`.** `Il2CppTypedCilVerifier`, ECMA-335 doğrulama tipleriyle
   (int32, int64, native int, F, O, &, değer tipi, generic) her yolu ve dal
   birleşimini izleyen bir soyut yorumlayıcıdır. Her store, argüman, alan erişimi
   ve return bildirilen tiple karşılaştırılır. Desteklenmeyen opcode, exception
   region, generic çağrı/alan ve vararg reddedilir. Cpp2IL'in kendi AsmResolver
   `RuntimeContext`'i verildiğinde nesne referansı yalnız çözülmüş tipi, bir base tipi
   veya implemente ettiği bir interface bildirilen tip olduğunda kabul edilir. Aynı
   kontrol çağrı receiver'ına ve alan sahibine de uygulanır; çözülemeyen tip reddedilir.
6. **Güvenilir lifter.** `RecoveredNative` yalnız x86-64 lifter yolunda verilir. ARMv8
   yolunda bütün kapılardan geçen gövde `UntrustedLifter` ile stub kalır ve yalnız kanıt
   olarak tutulur (bkz. [P11 kapanışı](#p11-kapanışı)).

Hepsini geçen gövde `RecoveredNative`, diğerleri minimal stub olur. Sayaçlar:
`GeneratedStub`, `NoBodyByDesign`, `Unknown=0`, `RecoveredNative`. Metot kaydı ayrıca
kullanılan lifter'ı (`Lifter`) ve generic metotlar için instantiation listesini
(`GenericInstantiations`: tip argümanları, pointer, RVA, adresi paylaşan metot sayısı)
taşır. Ana süreç şunları
reddeder:

- Kapı zinciri `IsilChecked > StackHeightChecked > TypedCilChecked` ile bitmeyen `RecoveredNative`.
- Generic veya paylaşılan adresli `RecoveredNative`.
- Sayaçla tutmayan kayıt sayısı.

Gerçek corpus'taki kapı sonuçları:

| Metot | Kapı zinciri | LEA | Çıkarılan tip | Replay |
|---|---|---|---|---|
| `Counter.Step` | `IsilChecked > StackHeightChecked > TypedCilChecked` | yok | 0 | 3/3 |
| `Affine` | aynı | `Applied` | 2 | 5/5 (`int.MaxValue` taşması dahil) |
| `Branch` | aynı | `Applied` | 3 | 4/4 |
| `Sum` | aynı | `Applied` | 6 | 4/4 |
| `Divide` | `RejectedIsil` (`UnresolvedCallTarget`) | `Partial` | 2 | stub |
| `Echo<T>` | `SharedGenericBody` | — | — | stub |
| `Counter..ctor` | `UnsupportedConstructor` | — | — | — |

### Sidecar kanıtı

Seçili her metot için `Evidence/NNNNN.txt` dosyası yazılır. İçeriği: imza, token,
pointer/RVA/raw offset, köken, kapı zinciri, LEA sonucu, çıkarılan tip sayısı,
tanı, native disassembly, analiz sonrası ISIL ve üretilen CIL. Bölüm başına en fazla
2048 satır, dosya başına 256 K karakter tutulur. Dosya adları sıra numarasıdır.
Ana süreç dosyaları yüklemez; yol biçimini, link olmadığını, uzunluğu ve SHA-256'yı
doğrular.

### Worker kaynakları

- `MaxHeapMegabytes` (64–1048576): .NET worker'a `DOTNET_GCHeapHardLimit` olarak
  (onaltılık bayt) verilir. 64 MiB'de değer `0x4000000` olarak çocuğa ulaştı;
  sınırı aşan double durduruldu ve `ExitedWithError` alındı. Native bellek kapsanmaz.
- `RetainInputSnapshots` (varsayılan `true`): `false` ise yalnız `Completed` işin
  `Input/` kopyaları yanıt doğrulandıktan sonra silinir ve `run.json` içinde
  `InputSnapshotsRetained=false` yazılır. Başarısız işler kopyaları her zaman tutar.

## Süreç ve protokol sözleşmesi

**Job dizini.** `WorkDirectory` verilmezse
`$TMPDIR/AssetRipper/Il2CppJobs/<32 hex GUID>/` kullanılır. Girdiler önce kopyalanır:
`Input/native.bin`, `Input/global-metadata.dat` ve gerekirse
`Input/Version/<globalgamemanagers|data.unity3d|UnityPlayer.bin>`. Her kopyanın
uzunluğu ve SHA-256'sı istekte yer alır. Worker yalnız tam olarak bu job
yollarındaki dosyaları kabul eder, hash'leri analizden önce ve sonra doğrular.
Ana süreç yanıtı aldıktan sonra yeniden doğrular. Kaynak dosyanın kopyalamadan
sonra değişmesi işi etkilemez.

**Başlatma.** Worker yolu mutlak olmalıdır. `.dll` uzantısı `dotnet <dll>`, diğerleri
doğrudan çalıştırılır; `dotnet` bu durumda `PATH` üzerinde bulunmalıdır. Argümanlar
shell'siz `ArgumentList` ile verilir. Süre sınırı 1–86400 saniye, varsayılanı 300
saniyedir.

**Durdurma.** Süre dolduğunda veya iptal edildiğinde `Kill(entireProcessTree: true)`
uygulanır; worker'ın başlattığı alt süreç de durdurulur. Başlatmadan önce iptal
edilen işte çocuk süreç oluşmaz. stdout/stderr her biri 64 KiB ile sınırlıdır ve
kesilme işaretlenir. Pipe boşaltma 5 saniyeyle sınırlıdır.

**Belge biçimi.** İstek ≤ 64 KiB, yanıt ≤ 16 MiB. Protokol şema sürümü **2**;
`Il2CppRecovery` rapor şeması 1'de kaldı, yalnız opsiyonel alanlar eklendi. JSON
source-generated `System.Text.Json` ile okunur; reflection fallback yoktur ve
tanınmayan alanlar reddedilir. Yazma `.tmp` + atomik taşıma ile yapılır.

**Durumlar.** `InputFailed`, `StartFailed`, `TimedOut`, `Cancelled`,
`ExitedWithError`, `InvalidResponse`, `Completed`; manager tarafında ayrıca
`InvalidAssembly`. Sıfırdan farklı exit kodu, diskte geçerli görünen bir yanıt
dosyası olsa bile başarı sayılmaz. Worker exit kodları:

- 0: `Completed`
- 1: `AnalysisFailed`
- 2: `RejectedInput`
- 3: `RejectedPair`

Hata mesajı sağlayıcının iç istisnasını da taşır. Her iş `run.json`, `stdout.log`
ve `stderr.log` bırakır; managed staging reddi ayrıca `manager.json` yazar. Job
dizinleri kendiliğinden silinmez.

**Girdi reddi.** Native analizden önce metadata başlığı kontrol edilir:
`0xFAB11BAF` magic değeri ve sürüm ≥ 16. Şifreli veya desteklenmeyen metadata,
Cpp2IL çalışmadan `RejectedInput` olur.

**Yanıt doğrulaması.** Ana süreç şunları reddeder:

- Job ID, PID, native/metadata kimliği veya sürüm kanıtı dosyaları istekle uyuşmayan yanıt.
- Ayrıştırılamayan Unity sürümü; açık sürüm verildiyse farklı sürüm.
- Protokol şeması ≠ 2.
- `Recovery.SchemaVersion != 1`, `Validation != NotRun`, farklı content level,
  0 veya 4096'dan fazla assembly.
- Worker'ın kendi doldurduğu `Isolation` alanı; bu alanı yalnız ana süreç yazar.
- Eksik veya `Passed`/`Partial` dışında `PairValidation`; sıfırdan farklı `Unknown`.
- Metadata-only işte tam `AsmResolverDllOutputFormatDefault` / `Cpp2IL.Core` /
  `dll_default` dışında provider, metot kaydı, `RecoveredNative` veya beklenen
  dışında `RecoveryCapability`.
- `BehaviorValidation != NotRun` ya da `GeneratedStub`, `RecoveredNative`,
  `NoBodyByDesign` dışında köken (eski `CandidateUnverified` dahil).
- Tekrarlanan çıktı yolu veya assembly adı; `Assemblies/NNNNN.dll` dışında yol,
  symlink/reparse point ya da uzunluk/hash uyuşmazlığı. Aynı kurallar
  `Evidence/NNNNN.txt` sidecar'larına da uygulanır.

## Ürün bağlantısı

`AssemblyManagerFactory.Create` seçimi açıktır: IL2CPP + worker →
`IsolatedIL2CppManager`, IL2CPP + worker yok → `IL2CppManager`. Mono ve Unknown
backend'leri ayar verilse bile değişmez.

Ana süreç çıktıyı görünür yapmadan önce bütün assembly'leri hazırlar. Baytları
yeniden hash'ler, `AssemblyDefinition.FromBytes(..., createRuntimeContext: false)`
ile yükler, adları manifest ile karşılaştırır ve metot gövdesi sayılarını kendi
hesabıyla worker raporuna eşitler. Ardından `mscorlib` ile `RuntimeContext` kurar.
Bu adımlardan biri başarısız olursa manager hiçbir assembly göstermez; durum
`InvalidAssembly` olur ve `Il2CppWorkerException` fırlatılır.

`Il2CppRecovery.Isolation` (`JobId`, `ProcessId`, `Mode=Process`,
`ProtocolValidation=Passed`), `PairValidation` ve `RecoveryCapability`
`script-export-report.json` içine yazılır. Bunlar süreç, protokol ve yapı
doğrulamasını anlatır; davranış doğrulaması değildir.

Worker yapılandırılmışken `GameStructure`, başlatma hatasında `Unknown` backend'e
geçmez. Hata, kanıt dizinini içeren `Il2CppWorkerException` olarak yayılır.

Ana süreçte sürüm boşsa istemci sırasıyla `globalgamemanagers`, `data.unity3d` ve
`UnityPlayer` dosyalarından ilk bulunanı sürüm kanıtı olarak kopyalar. Sürümü
çocuk süreç Cpp2IL ile belirler. Hiçbir kanıt dosyası yoksa iş çocuk süreç
başlamadan `InputFailed` olur.

Level3'te `IL2CppManager.RecoveryOutputFormat` veya `RecoveryProcessingLayers`
hook'u ayarlıysa izole manager `NotSupportedException` verir.

`Il2CppWorkerOptions` (heap ve girdi alanları dahil) `SerializedSettings`
kaydet/yükle döngüsünden aynı değerle dönüyor. GUI'ye ayar eklenmedi.

SystemTester seçenekleri: `--il2cpp-worker FILE`, `--il2cpp-worker-dir DIR`,
`--il2cpp-worker-timeout SECONDS`, `--il2cpp-worker-max-heap MIB`,
`--il2cpp-worker-discard-inputs`. Worker dışındaki seçenekler worker olmadan
verilirse kullanım hatası alınır. Worker üzerinden import edilen IL2CPP
vakalarının `Il2CppWorker` kaydı, hata durumunda da rapora eklenir.

## Çalıştırma

Test projesi worker projesine `ReferenceOutputAssembly="false"` ile bağlıdır;
testleri derlemek worker'ı da `Source/0Bins/AssetRipper.Tools.Il2CppWorker/<Configuration>/`
altına derler.

```sh
python3 TestProjects/build_il2cpp_fixture.py --unity /path/to/6000.4.0f1/Editor/Unity \
  --output /absolute/new-empty/corpus
python3 TestProjects/build_il2cpp_fixture.py --unity /path/to/6000.4.0f1/Editor/Unity \
  --output /absolute/new-empty/pair-variant --pair-variant
python3 TestProjects/build_il2cpp_fixture.py --unity /path/to/6000.4.0f1/Editor/Unity \
  --output /absolute/new-empty/stripped --stripped
python3 TestProjects/build_il2cpp_fixture.py --unity /path/to/6000.4.0f1/Editor/Unity \
  --output /absolute/new-empty/arm64 --target android-arm64 \
  --adb /path/to/platform-tools/adb --emulator /path/to/emulator --avd <arm64-capable-avd>
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter 'TestCategory=Il2CppWorker' --logger trx
ASSETRIPPER_UNITY_IL2CPP_FIXTURE=/absolute/corpus \
ASSETRIPPER_UNITY_IL2CPP_PAIR_VARIANT_FIXTURE=/absolute/pair-variant \
ASSETRIPPER_UNITY_IL2CPP_STRIPPED_FIXTURE=/absolute/stripped \
ASSETRIPPER_UNITY_IL2CPP_ARM64_FIXTURE=/absolute/arm64 \
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter 'TestCategory=Il2CppWorker|TestCategory=Il2CppWorkerNative|TestCategory=Il2CppCapability' --logger trx
```

`--pair-variant`, kopya projede `PureMethods.Affine`'den sonra korunmuş tek bir
metot ekler. `--stripped`, `[Preserve]` ve `link.xml` girdilerini kaldırıp High
managed stripping ile build eder. `--target android-arm64` yalnız IL2CPP ARM64 APK'sı
üretir ve oyuncuyu builder'ın açıp kapattığı emulator örneğinde çalıştırır. Builder
her biçimde aynı 20 davranış vakasını oracle ile doğrular.
Ortam değişkenleri yoksa ilgili native vakalar açıkça atlanır; yapılandırılmış ama
bozuk corpus skip sayılmaz.

```sh
dotnet run --project Source/AssetRipper.Tools.SystemTester -- /path/to/Game \
  --il2cpp-worker /absolute/AssetRipper.Tools.Il2CppWorker.dll \
  --il2cpp-worker-dir /absolute/jobs --il2cpp-worker-timeout 300 \
  --il2cpp-worker-max-heap 4096 --il2cpp-worker-discard-inputs \
  --output /path/to/Results --report /path/to/game.json
```

Gerçek corpus kanıtı `TestResults/il2cpp-worker/<Kind>-<GUID>/` altında job
dizinleri ve `integration.json` olarak tutulur. Deneysel aday vakası önce run ve
replay sonucunu yazar, sonra assert eder. Job dizinleri native binary ve metadata
kopyalarını içerdiği için CI artifact yollarına eklenmedi.

## Testler

**`Il2CppWorker` (75, Unity gerektirmez).** Test içinde derlenen kontrollü bir worker
double'ı oyun binary'si okumaz ve çalıştırmaz.

- Süreç: geçerli yanıt, stdout/stderr taşması, sıfırdan farklı exit, yanıt yazıp
  exit 37, iki süre aşımı, iki iptal, eksik worker, çıktı symlink'i, kaynak
  değişimi, özel karakterli yol, gerçek worker'ın bozuk metadata reddi, sürüm
  kanıtı eksikliği, managed staging reddi.
- 18 geçersiz yanıt türü. İlk 12'si ilk dilimden; yeni olanlar: eski şema, eksik
  ve başarısız çift doğrulaması, `Unknown≠0`, metadata-only `RecoveredNative`
  iddiası, yanlış yetenek.
- Aday kuralları: geçerli aday yanıtı ve doğrulanan sidecar'ı; eksik kapı, tipsiz
  kapı, generic, paylaşılan adres, sayaç uyuşmazlığı, eski `CandidateUnverified`
  kökeni, sidecar yol kaçışı ve hash uyuşmazlığı reddi.
- Kaynaklar: heap değerinin çocuğa `0x4000000` olarak ulaşması, sınırsızken değişkenin
  yokluğu, sınırı aşan double'ın durdurulması, aralık dışı değerlerin başlatmadan
  reddi, girdi kopyalarının yalnız başarılı işte silinmesi.
- Ayar serileştirmesi; dört backend/ayar seçimi.
- 8 yapısal kapı vakası ve 8 tipli doğrulayıcı vakası: iyi tipli aritmetik, dal
  birleşiminde farklı derinlik ve tip, object local'e int32, return'de kalan değer,
  yanlış genişlikte argüman, yerel enum'un alt tipi, desteklenmeyen opcode. Eski
  `ldstr`-as-`Int32` örneği artık tipli kapıda reddediliyor.
- 6 referans hiyerarşisi vakası: türetilmiş → base ve interface kabul; ilgisiz tip
  ve ters yön red; bağlamsız eski davranış; ilgisiz receiver red.

**`Il2CppWorkerNative` (12, gerçek corpus).**

- Normal oyun yüklemesi ve çocukta sürüm tespiti.
- Eksik worker'da `Unknown`'a düşmeme.
- İki ardışık metadata işi, çift doğrulaması sayıları dahil.
- Bozuk native girdiden sonra temiz iş.
- Üç corpus'ta (orijinal, stripped, ARM64) ortak aday denemesi:
  - Sayaçlar aynı corpus'un metadata-only işiyle tutmalı.
  - Lifter adı, `UntrustedLifter` listesi ve generic instantiation matrisi kontrol ediliyor.
  - Davranış kapısı: her `RecoveredNative` gövde kendi oracle gözlemlerinin tamamını
    eşleştirmeli. `Step` kurtarıldıysa build'de bulunan her dispatch yüzeyi de eşleşmeli.
- İki yönde yanlış çift ve varyantın geçerli çifti.
- İki yönde çapraz mimari çift (`AnalysisFailed`).

## Test turunda bulunan hatalar

Testsiz yazılan ikinci dilim kodunda test turu şunları buldu; hepsi düzeltildi:

1. **Geçerli çifte sahte red.** Doğrulayıcı modül sırasının image sırasıyla aynı
   olmasını istiyordu; gerçek corpus'ta mscorlib image 0, modül 48. Metadata 27+
   LibCpp2IL modülü adla buluyor. Kontrol yalnız 24.2–27 aralığına ve
   `assemblyIndex`'e indirildi.
2. **LEA dönüşümünde `OverflowException`.** Negatif displacement'ın `ulong` →
   `long` dönüşümü projenin checked aritmetiğinde taşıyordu; `unchecked` yapıldı.
3. **Yalnız-index LEA.** `lea eax,[rdi*8]` lifter'da `[]` oluyordu ve hizalama dışında
   kalıyordu. Kayıplı biçimler hizalamaya alındı, yazmaçlar Iced'den kuruldu.
4. **Tipsiz geçici değerler.** `Branch` tipli kapıda doğru biçimde reddediliyordu,
   ama nedeni sağlayıcının eksik tip yayılımıydı. Dar tip çıkarımı eklendi.

Ayrıca LibCpp2IL'in başlatma hatası artık iç istisnasıyla raporlanıyor. Daha önce
yalnız "Fatal Exception" görünüyordu; şimdi `IndexOutOfRangeException` gibi gerçek
neden de görünüyor.

## Doğrulama kaydı

**İlk dilim.** Hedefli Debug 73/73 (2026-10-20 18:06).

**Üçüncü dilim (kapanış).** 2026-10-20, Debug: IL2CPP kategorileri **116/116**
(`Il2CppWorker` 75, `Il2CppWorkerNative` 12, `Il2CppCapability` 28, `Il2CppNative` 1),
atlanan yok. Bütün corpus'lar (yeni stripped ve ARM64 dahil) ve Unity Editor etkin tam
solution Debug **911/911** (8 proje, build 0 uyarı / 0 hata); Release **910/910**
(bilinen Debug.Assert vakası dışarıda; build 0 hata / 1 mevcut Animator TODO uyarısı).

**İkinci dilim.** 2026-10-20, Debug, `--no-restore -p:AllowMissingPrunePackageData=true`:

- `Il2CppWorker`: 69/69.
- Corpus etkin `Il2CppWorkerNative`: 8/8.
- Bütün corpus'lar ve Unity Editor etkin tam solution Debug: 8 test projesinde
  **901/901**, atlanan yok. Solution Debug build **0 uyarı / 0 hata**.
- Release: **900/900**; Debug.Assert'e bağlı bilinen
  `ExportIdHandlerTests.GetMainExportID_ValueGreaterThan100000_DebugAssertFails`
  dışarıda bırakıldı. Release build **0 hata / 1 mevcut Animator TODO uyarısı**.
- SystemTester uçtan uca, doğru çift, worker + `--il2cpp-worker-max-heap 4096
  --il2cpp-worker-discard-inputs`:
  - Worker `Completed`, çift `Passed`, `InputSnapshotsRetained=false`, 49 assembly.
  - Vaka sonucu worker'sız koşuyla aynı: 21/21 koleksiyon export edildi. Mevcut
    `UnityEngine.GUISkin` MonoBehaviour okuma hatası ("Script ID is invalid")
    yüzünden `MonoBehaviourFailures=1` ve vaka başarısız. Bu durum worker'dan bağımsız.
  - İki worker'sız koşu kendi aralarında aynı 68 dosyada ayrışıyor: DLL MVID'leri
    ve `.meta` GUID'leri. Worker koşusu yalnız bu 68 dosyada ayrışıyor; decompile
    edilmiş `.cs` dosyaları birebir aynı.
- SystemTester uçtan uca, sessiz yanlış çift (varyant binary + orijinal metadata):
  - Worker ile: `RejectedPair`, 0 assembly, vaka başarısız.
  - Worker'sız in-process import: çift fark edilmedi ve 49 assembly yüklendi.
  - Metadata-only çıktı native adres taşımadığı için kayma bu DLL'lerde görünmüyor;
    risk gövde kurtarma ve adres kullanan yollar için geçerli.

Koşulmayanlar: Windows/macOS ve uzak CI.

## Sınırlar ve açık kabul kapıları

- Ayrı süreç çökmeyi ve global Cpp2IL durumunu sınırlar; güvenlik sınırı değildir.
  Heap sınırı yalnız managed heap içindir.
- Job dizinleri kendiliğinden silinmez. `RetainInputSnapshots=false` yalnız başarılı
  işin girdi kopyalarını siler.
- Otomatik retry yoktur.
- P10 capability deneyi (`Il2CppNative`) hâlâ Cpp2IL'i test host'unda çalıştırır.
  Worker'daki deneysel aday vakası, izole yoldaki karşılığıdır.
- Çift kontrolü sayı tabanlıdır; aynı şekle sahip iki build ayırt edilemez.
- Tipli doğrulayıcı generic instantiation'ı ve exception region'ı kapsamaz.
  Referans hiyerarşisi yalnız çözülebilen tiplerle kontrol edilir; çözülemeyen tip
  reddedilir. `RecoveredNative` davranış doğruluğu anlamına gelmez; bu corpus'larda
  davranışı ayrıca replay doğruladı.
- ARMv8'de kurtarma yok: sağlayıcının yeni lifter'ı tip açısından geçerli ama yanlış
  gövde üretiyor (`UntrustedLifter`). Düzeltmesi sağlayıcı lifter'ında ayrı bir iş.
- Native C++ exception region rekonstrüksiyonu (`Divide`) ve upstream sağlayıcı
  karşılaştırması araştırma kapsamında.
- Doğrulanan dilim: Unity 6000.4.0f1 / metadata 39. Linux x86_64'te normal ve
  stripped, Android ARM64'te yapı ve metadata. Başka Unity sürümü yerelde
  kurulu olmadığı için ölçülmedi.
