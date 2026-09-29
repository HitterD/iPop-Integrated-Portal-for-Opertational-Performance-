// Starter content for new pages (ProseMirror JSON). The server hands each seeded page its template
// exactly once (POST /seed); the first editor to claim it writes this into the shared Yjs doc.

const txt = (text, ...marks) => ({ type: 'text', text, ...(marks.length ? { marks: marks.map(m => typeof m === 'string' ? { type: m } : m) } : {}) });
const b = s => txt(s, 'bold');
const i = s => txt(s, 'italic');
const code = s => txt(s, 'code');
const color = (s, c) => txt(s, { type: 'textStyle', attrs: { color: `var(--nt-${c})` } });
const hl = (s, c) => txt(s, { type: 'textStyle', attrs: { backgroundColor: `var(--nb-${c})` } });
const inl = parts => parts.map(x => typeof x === 'string' ? txt(x) : x);

export const p = (...parts) => ({ type: 'paragraph', ...(parts.length ? { content: inl(parts) } : {}) });
export const h = (level, ...parts) => ({ type: 'heading', attrs: { level }, content: inl(parts) });
const todos = items => ({ type: 'taskList', content: items.map(it => ({ type: 'taskItem', attrs: { checked: false }, content: [p(...[].concat(it))] })) });
const bullets = items => ({ type: 'bulletList', content: items.map(it => ({ type: 'listItem', content: [p(...[].concat(it))] })) });
const numbers = items => ({ type: 'orderedList', attrs: { start: 1 }, content: items.map(it => ({ type: 'listItem', content: [p(...[].concat(it))] })) });
const callout = (emoji, colorKey, ...paras) => ({ type: 'callout', attrs: { emoji, color: colorKey }, content: paras });
const toggle = (summary, ...content) => ({ type: 'details', content: [
  { type: 'detailsSummary', content: inl([].concat(summary)) },
  { type: 'detailsContent', content },
] });
const widget = kind => ({ type: 'noteWidget', attrs: { kind } });
const hr = { type: 'horizontalRule' };
const quote = (...parts) => ({ type: 'blockquote', content: [p(...parts)] });
const doc = (...content) => ({ type: 'doc', content });

/// The phrase the tutorial's example comment is anchored to.
export const GUIDE_COMMENT_TEXT = 'klik teks kuning ini untuk melihat komentar';

const guide = () => doc(
  callout('👋', 'orange', p('Selamat datang di ', b('Notes'), ' — tempat menyimpan catatan, SOP, notulen rapat, dan apa saja. Halaman ini adalah ',
    b('contoh yang bisa langsung kamu coba'), ': centang tiap langkah setelah mencobanya. Tenang, tidak ada yang bisa rusak — halaman ini hanya milikmu.')),
  widget('progress'),

  h(2, '1️⃣ Menulis'),
  todos([
    ['Klik di mana saja lalu mulai mengetik. ', b('Tersimpan otomatis'), ' — tidak ada tombol Simpan.'],
    ['Klik emoji 👋 di atas judul untuk mengganti ikon halaman.'],
    ['Ketik ', code('/'), ' di baris kosong untuk memilih jenis blok: judul, daftar, to-do, gambar, kode, dan lainnya.'],
  ]),
  p(i('Coba di sini → ')),
  toggle(['Lebih cepat dengan shortcut Markdown ', color('— klik panah untuk membuka', 'gray')],
    bullets([
      [code('# '), ' judul besar · ', code('## '), ' judul sedang · ', code('### '), ' judul kecil'],
      [code('- '), ' daftar bullet · ', code('1. '), ' daftar bernomor · ', code('[ ] '), ' to-do'],
      [code('> '), ' kutipan · ', code('```'), ' blok kode · ', code('---'), ' garis pemisah'],
      [code('@'), ' sebut rekan kerja — dia mendapat notifikasi'],
    ])),

  h(2, '2️⃣ Menyusun halaman'),
  todos([
    ['Arahkan kursor ke sebuah blok — muncul ⠿ di kirinya. ', b('Seret'), ' untuk memindahkan, ', b('klik'), ' untuk ubah jenis, warna latar, atau hapus. Coba pada baris ini!'],
    ['Buat halaman di dalam halaman — ketik ', code('/page'), ' atau klik ', b('+'), ' di sebelah nama halaman di sidebar.'],
  ]),

  h(2, '3️⃣ Warna & gaya'),
  p('Setiap halaman punya ', b('warna aksen, cover, dan font'), ' sendiri — tidak harus hijau semua 🙂 Warna aksen dipakai untuk checklist, mention, kutipan, dan penomoran.'),
  todos([['Ganti warna aksen halaman ini — klik salah satu:']]),
  widget('accents'),
  todos([
    ['Klik ', b('Style'), ' di kanan atas untuk mengganti cover, font (Default · Serif · Mono), dan lebar halaman.'],
    [b('Blok teks ini'), ', lalu klik ', b('A'), ' di toolbar yang muncul untuk memberi ', color('warna teks', 'red'), ' atau ', hl('sorotan', 'yellow'), '.'],
  ]),
  p(color('Callout berwarna untuk menandai hal penting — warnanya bisa diganti lewat ⠿:', 'gray')),
  callout('💡', 'blue', p(b('Tips'), ' — info yang membantu')),
  callout('⚠️', 'yellow', p(b('Perhatian'), ' — cek sebelum lanjut')),
  callout('⛔', 'red', p(b('Penting'), ' — jangan dilewati')),
  callout('✅', 'green', p(b('Selesai'), ' — sudah dikerjakan')),

  h(2, '4️⃣ Berbagi & edit bersama'),
  p('Note bisa dikirim ke ', b('chat pribadi maupun grup'), '. Semua anggota chat langsung bisa membukanya — dan kalau diberi izin ', i('Can edit'), ', kalian bisa menulis ', b('bersamaan secara langsung'), '.'),
  numbers([
    ['Klik ', b('Share'), ' di kanan atas.'],
    ['Pilih tab ', b('Send to chat'), ', lalu pilih DM atau grup.'],
    ['Tentukan izinnya: ', b('View'), ', ', b('Comment'), ', atau ', b('Edit'), '.'],
    ['Klik ', b('Send'), ' — note muncul sebagai kartu di chat.'],
  ]),
  widget('perms'),
  callout('👥', 'green', p(b('Akses mengikuti anggota chat.'), ' Anggota baru grup otomatis bisa membuka note; yang keluar dari grup kehilangan akses. Sub-halaman ikut terbagi.')),
  p('Saat orang lain sedang mengedit, kursor dan namanya terlihat langsung. Avatar mereka juga muncul di kanan atas.'),
  todos([['Kirim sebuah note ke chat, lalu buka lagi dari kartunya di chat.']]),

  h(2, '5️⃣ Komentar & mention'),
  todos([
    ['Pilih teks lalu klik ', b('Comment'), ' (atau ', code('Ctrl Shift M'), '). Contoh: ', GUIDE_COMMENT_TEXT, '.'],
    ['Ketik ', code('@'), ' untuk menyebut rekan — dia akan mendapat notifikasi di iPop.'],
  ]),

  h(2, '6️⃣ Gambar, file & riwayat'),
  todos([
    ['Seret atau tempel gambar/file langsung ke halaman — tersimpan aman di NAS kantor.'],
    ['Salah hapus? Buka ', b('⋯ → Version history'), ' untuk melihat dan ', b('mengembalikan'), ' versi lama.'],
  ]),
  hr,
  toggle('⌨️ Semua shortcut keyboard', bullets([
    [code('Ctrl K'), ' cari note · ', code('Ctrl B'), ' tebal · ', code('Ctrl I'), ' miring · ', code('Ctrl U'), ' garis bawah'],
    [code('Ctrl Shift M'), ' komentar · ', code('Ctrl Z'), ' undo · ', code('Tab'), ' masuk (indent)'],
  ])),
  callout('🙋', 'gray', p('Masih bingung? Tanya tim ICT lewat chat.')),
);

const meeting = () => {
  const today = new Date().toLocaleDateString('id-ID', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
  return doc(
    callout('📅', 'blue', p(b('Tanggal: '), today), p(b('Peserta: '), '@')),
    h(2, 'Agenda'), numbers([[''], [''], ['']]),
    h(2, 'Catatan'), bullets([['']]),
    h(2, 'Keputusan'), callout('✅', 'green', p('')),
    h(2, 'Action items'), todos([[''], ['']]),
  );
};

const todo = () => {
  const day = d => new Date(Date.now() + d * 864e5).toLocaleDateString('id-ID', { weekday: 'long', day: 'numeric', month: 'short' });
  return doc(
    h(2, `Hari ini · ${day(0)}`), todos([[''], ['']]),
    h(2, `Besok · ${day(1)}`), todos([['']]),
    h(2, 'Nanti'), todos([['']]),
  );
};

const brief = () => doc(
  callout('🎯', 'purple', p(b('Tujuan: '), 'apa yang ingin dicapai dan kenapa penting.')),
  h(2, 'Ruang lingkup'), bullets([['Termasuk: '], ['Tidak termasuk: ']]),
  h(2, 'Timeline'), todos([['Minggu 1 — '], ['Minggu 2 — '], ['Minggu 3 — ']]),
  h(2, 'Penanggung jawab'), bullets([['Owner: @'], ['Tim: ']]),
  h(2, 'Risiko & pertanyaan terbuka'), bullets([['']]),
);

const sop = () => doc(
  callout('📖', 'orange', p(b('Tujuan SOP: '), 'jelaskan singkat untuk apa prosedur ini.')),
  h(2, 'Sebelum mulai'), todos([['Siapkan '], ['Pastikan ']]),
  h(2, 'Langkah-langkah'), numbers([[''], [''], ['']]),
  callout('⚠️', 'yellow', p(b('Perhatian: '), 'hal yang sering terlewat.')),
  h(2, 'Jika terjadi masalah'), bullets([['Hubungi ']]),
);

export const TEMPLATES = { guide, meeting, todo, brief, sop };
