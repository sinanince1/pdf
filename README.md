<p align="center"><img src="src/PanDaF/Assets/PanDaF.png" width="112" alt="PanDaF"></p>
<h1 align="center">PanDaF</h1>
<p align="center">Öğretmenler için basit, Türkçe PDF araçları · Windows 10 / 11</p>

<p align="center"><a href="https://github.com/sinanince1/pdf/releases/latest/download/PanDaF.exe"><b>⬇ PanDaF'ı indir (Windows)</b></a></p>

## Neler yapabilir?

| Araç | Ne işe yarar? |
| --- | --- |
| **PDF birleştir** | Birden fazla PDF'yi istediğiniz sırayla tek dosyada toplar. |
| **PDF böl** | Her sayfayı ayrı PDF yapar, her N sayfada bir böler veya yalnızca istediğiniz sayfaları (ör. `1-3, 5`) alır. |
| **Sayfaları düzenle** | Sayfaları küçük resimleriyle gösterir; silme, sürükleyerek sıralama ve döndürme. Geri alma desteklidir. |
| **PDF'ye çevir** | Word, PowerPoint, Excel dosyalarını ve fotoğrafları PDF yapar. Fotoğraflar A4'e yerleştirilebilir, tek PDF'de birleştirilebilir. |
| **PDF'den çevir** | PDF'yi Word belgesine, her sayfası ayrı bir resme (JPG/PNG) veya düz metne dönüştürür. |

Dosyaları doğrudan pencereye sürükleyip bırakabilirsiniz: tek PDF bırakırsanız sayfa düzenleyici, birden fazla PDF bırakırsanız birleştirme, Word veya fotoğraf bırakırsanız PDF'ye çevirme açılır.

## Kurulum

1. Yukarıdaki bağlantıdan **PanDaF.exe** dosyasını indirin.
2. Dosyaya çift tıklayın. Kurulum gerekmez; dosyayı Masaüstüne veya istediğiniz klasöre koyabilirsiniz.
3. İlk açılışta Windows **"Windows bilgisayarınızı korudu"** diyebilir. **Ek bilgi**'ye, ardından **Yine de çalıştır**'a tıklayın. Bu uyarı, uygulamanın ücretli bir kod imzası taşımamasından kaynaklanır; bir kez onaylamanız yeterlidir.

> Okul bilgisayarında uygulama hiç açılmıyorsa, bilgisayarın yöneticisi bilinmeyen programları engellemiş olabilir. Bilişim sorumlusundan izin isteyin.

## Word, PowerPoint ve Excel dönüşümleri

- Bilgisayarda **Microsoft Office** kuruluysa dönüşümler Office'in kendisiyle yapılır; biçim en iyi şekilde korunur.
- Office yoksa ücretsiz **LibreOffice** bileşeni ilk kullanımda **bir kez** indirilir (yaklaşık 350 MB). Uygulama önce izninizi sorar. Bileşen bilgisayara kurulmaz, yalnızca `%LOCALAPPDATA%\PanDaF` klasörüne açılır.
- PDF'yi Word'e çevirmek, PDF'nin nasıl hazırlandığına bağlıdır. Taranmış (fotoğraf gibi) sayfalarda düzen tam korunmayabilir.

## Gizlilik ve güvenlik

- Dosyalarınız **internete yüklenmez**; her şey bu bilgisayarda yapılır. İnternet yalnızca, Office olmayan bilgisayarda LibreOffice'in ilk indirilmesi için kullanılır.
- **Asıl dosyalarınız hiçbir zaman değiştirilmez.** Sonuçlar, asıl dosyanın bulunduğu klasöre yeni bir dosya olarak kaydedilir (aynı adda dosya varsa `Ad 2.pdf` gibi). O klasöre yazılamıyorsa `Belgeler\PanDaF` kullanılır.
- İşlem iptal edilirse veya hata olursa yarım dosya bırakılmaz.
- Doldurulabilir form alanı olan bir PDF'de, form alanlarının bozulabileceği işlemlerden önce uyarı gösterilir.

## Sorun mu var?

Hata ayrıntıları `%LOCALAPPDATA%\PanDaF\hata-gunlugu.txt` dosyasına yazılır. Sorun bildirirken bu dosyayı eklemeniz çözümü kolaylaştırır.

---

## Geliştirenler için

- **Yapı:** `src/PanDaF.Core` (PDF motoru), `src/PanDaF` (WPF arayüzü), `tests/PanDaF.Tests` (motor testleri). .NET 10, WPF, x64.
- **Derleme:** `dotnet publish src/PanDaF/PanDaF.csproj -c Release -o build/app` → tek dosya `PanDaF.exe` (.NET kurulumu gerekmez).
- **Testler:** `dotnet run --project tests/PanDaF.Tests -c Release`. `PANDAF_LIBREOFFICE=1` ile LibreOffice gerçekten indirilir ve Office dönüşümleri de denenir.
- **Arayüz testi:** `PanDaF.exe --smoke <klasör>` tüm araçları gerçek pencerede, gerçek dosyalarla çalıştırır ve ekran görüntüleri alır.
- **Otomatik derleme:** Her `main` gönderiminde GitHub Actions Windows'ta testleri ve arayüz testini çalıştırır. Yeni sürüm için GitHub'da **Releases → Draft a new release** ile `v1.0.1` gibi bir sürüm yayınlayın; `.exe` ve `.zip` derlenip sürüme otomatik eklenir.

## Teşekkür ve lisans

PanDaF, [Orkun Işıtmak](https://github.com/orkunisitmak)'ın açık kaynaklı [Nashi](https://github.com/orkunisitmak/nashi-app) uygulamasının PDF motoru temel alınarak hazırlanmıştır. Nashi'nin sayfa birleştirme/bölme/sıralama motoru, PDFium ile sayfa çizimi, LibreOffice'in doğrulanmış kurulumu ve PDF'den metin çıkarma kodları uyarlanmış; arayüz öğretmenler için yeniden yazılmıştır.

[MIT lisansı](LICENSE) · [Üçüncü taraf bileşenler](THIRD-PARTY-NOTICES.txt) · Sinan İnce 2026
