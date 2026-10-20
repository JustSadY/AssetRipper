# P14 — Collection ve bağımlılık kapsamını koruyan seçici export

Bu dilim `UnityProject` ve `PrimaryContent` export yollarına ortak bir seçim planı
ekler. Girdi yükleme, processor'lar ve collection hazırlığı hâlâ bütün girdi üzerinde
çalışır. Filtre, hazırlanmış collection'ların hangilerinin yazılacağını belirler;
büyük oyunlarda daha az RAM ile import veya streaming import sağladığı iddia edilmez.

## Kullanım ve eşleştirme

`FullConfiguration.ExportSettings.Selection` alanında `ClassIds`, `Names`, `PathIds`
ve `Collections` dizileri bulunur. Aynı alandaki değerler OR, farklı alanlar AND ile
eşleşir. İsimler `GetBestName()` sonucuyla, collection isimleri kaynak collection
adıyla tam ve büyük/küçük harfe duyarlı karşılaştırılır. PathID yalnız kendi
collection'ı içinde anlamlıdır; tek başına kullanıldığında farklı dosyalardaki aynı
PathID'ler de kök olarak seçilebilir. Rapor kimlikleri bu yüzden collection, kaynak
yolu ve yalnız bu iş için geçerli `JobOrdinal` taşır.
Collection adları da çakışıyorsa doğrudan predicate ile `asset.Collection.FilePath`
kullanılabilir; CLI bütün eşleşen kökleri seçer.

```csharp
settings.ExportSettings.Selection = new()
{
    ClassIds = [114],
    PathIds = [12345],
    Collections = ["sharedassets0.assets"],
};
```

İki doğrudan exporter'ın `Export` çağrısına isteğe bağlı bir
`Predicate<IUnityObjectBase>` de verilebilir; yapılandırılmış filtreyle AND yapılır.
Predicate rapora serialize edilmez; gerçekten eşleşen kökler kaydedilir. Null seçim
veya bütün dizilerin boş olması eski tam export yolunu kullanır. Web UI için seçim
ekranı bu dilimin parçası değildir.
Mevcut üç parametreli `Export` imzaları korunur; predicate ayrı overload'dadır.

SystemTester aynı filtreleri tekrarlanabilen seçeneklerle sunar:

```sh
dotnet AssetRipper.Tools.SystemTester.dll /oyun/girdi \
  --select-class-id 114 --select-path-id 12345 \
  --select-collection sharedassets0.assets \
  --output /yeni/cikti --report /rapor/sonuc.json
```

`--select-name` de kullanılabilir. Geçersiz/eksik seçenek değeri çıkış kodu 2,
eksik veya başarısız export 1, başarılı iş 0 döndürür. Yeni/boş çıktı dizini kullanın;
API önceden yazılmış ilgisiz çıktıları temizlemez.

## Atomik sahiplik ve referans kapsamı

`ExportSelectionPlan`, doğrulanıp bir kez materialize edilmiş collection üyeliklerini
kullanır. Kök ve sonradan bulunan her referans hedefi kendi export collection'ının
tamamına yükseltilir. Böylece prefab veya sahnenin tek component'i seçilse bile
grubun diğer üyeleri korunur. Bu üyelerin ileri PPtr referansları da kuyrukla işlenir;
referans kimliğine göre ziyaret kümesi döngüleri sonlandırır. Null PPtr bağımlılık
değildir. Ters referanslar ve sadece string/path üzerinden keşfedilebilecek bağlantılar
aranmaz.

Script export'u mevcut assembly kapsamını koruyan tek atomik gruptur; tek bir script
seçimi assembly'deki diğer kaynakları da çıkarabilir. Primary-content'ın eski
"yalnız ilk MonoScript export'u tetikler" sahipliği düzeltildi: daha sonraki bir
MonoScript de aynı collection'a aittir. Referans/skip singleton'ları aynı instance'ı
kullandığı için bütün ilgisiz asset'ler yanlışlıkla tek gruba birleştirilmez.

`collection-export-report.json` içindeki `Selection` şunları taşır:

- `MatchedAssets`: predicate'in doğrudan seçtiği kökler.
- `IncludedAssets`: iş kimliği, ilk dahil edilme nedeni (`Requested`,
  `AtomicCollection`, `Dependency`) ve `CollectionIndex`.
- `Collections`: hazırlık indeksi, isim ve `Exportable` durumu. Dahil edilen her
  nesnenin ayrı bir dosyaya dönüşmesi beklenmez; gruplama, embedding, builtin referans
  ve skip davranışları ilgili exporter'ın mevcut sözleşmesidir.
- `Issues`: kaynağı/alanı ve FileID/PathID'siyle çözülemeyen referanslar; şeması
  bilinmeyen script/raw nesneler; bağımlılık okuma veya sahiplik hataları.

Sorun yoksa `DependencyCoverage=KnownObjectReferencesOnly` olur. Bu, bütün oyun
bağımlılıklarının bulunduğu iddiası değildir. Okunamayan/bilinmeyen MonoBehaviour,
opaque raw nesne, eksik referans veya kısmi dependency iterator hatası `Incomplete`
ve başarısız seçim üretir. Iterator hata vermeden önce bulunan kenarlar korunur.
Hiç eşleşme olmaması veya bağımsız çıktısı olmayan bir kökün seçilmesi de başarı
sayılmaz; ikinci bir başarılı kök bu durumu gizleyemez. Bilinen collection'lar yine
yazılır. I/O, iptal, bellek ve bunları sarmalayan fatal hatalar mevcut ExceptionPolicy
uyarınca dışarı taşar.

Hazırlıkta seçimin dışında kalan collection hataları subset raporundan çıkarılır;
seçili closure'daki hatalar korunur. Okunamayan seçili MonoBehaviour'ın ham payload'ı
mevcut SHA-256 kontrollü recovery yoluyla saklanır. Global import hataları,
assembly kimlik çakışmaları ve raw-recovery eksikleri muhafazakâr biçimde işin
başarısını etkilemeye devam eder.

Unity post-export aşamasında `path_id_map.json` seçili closure ile filtrelenir.
İndekslenmemiş `StreamingAssets` klasörünün topluca kopyalanması atlanır. Seçili
mesh/texture vb. için decoder'ın kullandığı normal resource stream yolları değişmez.
Proje scaffolding'i, paket ayarları, özgün assembly arşivi ve recovery/kanıt dosyaları
mevcut global kapsamını korur. Primary-content script seçimi de bütün assembly
decompile sözleşmesini kullanır; assembly başına kırpma yapılmaz.

## Gerçek sahnede bulunan iterator hatası

Pinli `AssetRipper.SourceGenerated` 1.4.0'ın `NavMeshSettings.FetchDependencies()`
iterator'ı `m_NavMeshData` pointer'ını sürekli tekrarlar. Null pointer da tekrar
edildiği için asset ziyaret kümesi bu iç döngüyü sonlandıramaz. İlk tam Debug koşusu
bu noktada elle durduruldu; ayrı 30 saniyelik kontrollü reproducer da süre sınırına
ulaştı. İkinci teşhis koşusunda on ardışık `m_NavMeshData (FileID=0, PathID=0)`
gözlendi ve yalnız teşhis için konan sınır iki sahne testinde açık hata üretti.

Seçim planı bu sınıfın referanslarını artık `Has_NavMesh()` / `Has_NavMeshData()`
ile mevcut alanlardan birer kez alır: eski `m_NavMesh` ve 5+ `m_NavMeshData`.
Unity 4.7 ve 6000.4 düzenleri için sentetik null/dolu pointer kontrolleri eklendi. Mevcut
alan ID'leri aynen korunur; eksik referans üretilmez. Paket pini yükseltilmedi ve
geçici trace/teşhis sınırı üretim kodunda bırakılmadı. Başarısız/durdurulan koşular
phase kanıtında korunur; tamamlanmış regresyonun yerine sayılmaz.

## Phase sonu doğrulama

Sentetik kontroller: döngülü/cross-file referans grafiği, aynı PathID'nin farklı
collection'larda bulunması, atomik grubun bağımlılıkları, kısmi iterator hatası,
fatal hatalar, opak nesne/okunamayan script, filtresiz tam export, filtre/predicate
birleşimi, boş seçim, desteklenmeyen kök, preparation izolasyonu, ham payload,
StreamingAssets kapsamı ve sonraki MonoScript sahipliği.

Gerçek Unity 6000.4 Linux Mono corpus'u: yalnız harici TextAsset seçimi iki exporter
ve iki şema kaynağında; eksik harici asset'in iki şema kaynağında açık raporu;
seçili component'ten prefab'a yükseltmenin TypeTree/assembly × Decompiled/Hybrid
Editor matrisi. Prefab checker iki script GUID/fileID bağını, inherited/private/
nested alanları, object-array/null referanslarını ve bilinen arithmetic/interface
sonuçlarını doğrular. Sahne seçimi TypeTree ve assembly fallback ile gerçek Editor'de
beş node, iki component ve 13 kullanıcı referansının oracle'ını kullanır. Bu sahne
corpus'u `RenderSettings.m_SpotCookie (FileID=2, PathID=10001)` engine resource girdisini
sağlamaz. İki sahne vakası bu yüzden export'u açık `Incomplete` / başarısız raporlar;
Editor'de kullanıcı grafiğinin okunabilmesi bu eksikliği kapatmaz. Tam engine-state
kabulü sayılmaz. Kontroller ayrıca
`IgnoreMe` asset'inin çıkmadığını doğrular.

Phase sonu doğrulaması tamamlandı:

| Kontrol | Sonuç |
|---|---|
| Tam solution Debug, 8 test projesi | **1021/1021** |
| Tam solution Release, 8 test projesi | **1020/1020** |
| Yeni `ExportSelection` kategorisi | **39/39 Debug ve Release** |
| Üç parametreli API köprüsü sonrası hedefli Debug kontrolleri | **6/6** |
| Son Debug ve Release solution derlemeleri | **0 uyarı / 0 hata** |
| CLI argüman/başarı/eksik seçim kontrolleri | **8/8 her yapılandırmada** |
| İki gerçek bundle ile tam/seçici path-ID haritası karşılaştırması | **4/4 iş** |

Release'te yalnız bilinen
`ExportIdHandlerTests.GetMainExportID_ValueGreaterThan100000_DebugAssertFails`
dışarıda bırakıldı. Tam koşuların vaka kümeleri yalnız bu testte ayrılıyor.
Corpus olmadan **27 geçti / 12 NotExecuted**; corpus varken yalnız Editor executable
olmadan **33 geçti / 6 NotExecuted**. Yapılandırılmış ama bozuk fixture ve başlatma
hataları skip'e çevrilmez.

Her yapılandırmada **402 corpus kopyası hash'i**, **104 import öncesi snapshot hash'i**,
**8 script GUID/fileID bağı**, **36 arithmetic/interface gözlemi**, **10 sahne node'u**
ve **26 kullanıcı referansı** denetlendi. İki eksik-harici-asset ve iki eksik-engine-
cookie sonucu beklenen `Incomplete` / başarısız export olarak kaldı. İki özgün
corpus'un toplam **57** manifest girdisi değişmedi. İki bundle'lı CLI karşılaştırması
355 yüklenen nesnede tam haritanın `ExternalTarget` + `GraphExternal`, seçici haritanın
yalnız `GraphExternal` içerdiğini Debug ve Release'te doğruladı.

İlk dört test setup hatası, NavMesh iterator'ında durdurulan koşu, kontrollü reproducer
ve tanı amaçlı başarısız sahne kontrolleri korundu. Eksik cookie tespitinden sonra
sahne testinin kabulü açık `Incomplete` sonucu ve bağımsız kullanıcı grafiği kontrolü
olarak tanımlandı; kayıp girdi başarıya çevrilmedi. Son tam koşuların yerine ara
sonuçlar sayılmadı. Native Editor hatasını gizleyen retry eklenmedi.

[Yerel phase kanıtı](../Source/0Bins/TestResults/P14-SelectiveExport-20261020/phase-verification.json)
TRX, CLI sonuçları, kaynak/paket hash'leri ve artifact denetimlerini taşır. Gerçek
Editor kanıtları `Source/0Bins/Other/AssetRipper.Tests/{Debug,Release}/TestResults/`
altındaki `unity-assembly-graph/Selection-*` ve `unity-scene/*-Selection-Editor-*`
dizinlerindedir. CI JSON ve seçilmiş Evidence ağacını alır; tam Editor logları ve
Library cache'leri alınmaz. Uzak CI çalıştırılmadı. Paket/SDK yükseltilmedi, Unity
kurulmadı ve commit atılmadı.

## Açık sınırlar

Tek sürüm/platform Mono kanıtıdır; genel Unity sürüm matrisi, Play Mode, IL2CPP native
metot kurtarma, AssetDatabase/dinamik path bağımlılıkları, adreslenebilir içerik
grafiği, arama/önizleme UI ve import belleği optimizasyonu bu kabulün dışındadır.
P12'nin gerçek 6000.5/v23 kabul kapısı açıktır. P13'ün genel asmdef/paket/ABI matrisi
ayrı iştir. Bu dilim için Unity kurulumu veya paket/SDK yükseltmesi gerekmedi.
