// ─────────────────────────────────────────────────────────────────────────────
// Configuración web de Firebase (es pública, no es un secreto: la seguridad la
// ponen las reglas de Firestore, que solo permiten LEER).
//
// Firebase Console → Configuración del proyecto → Tus apps → App web → SDK config.
// Pega aquí los valores. Si lo dejas en null, la página usa data/deals.json.
// ─────────────────────────────────────────────────────────────────────────────
window.PRECIOPIXEL_FIREBASE = {
  apiKey: "AIzaSyB4-oOMJMqr78lsioXnMV0TFRHQ2NSFxyA",
  authDomain: "multi-cor.firebaseapp.com",
  projectId: "multi-cor",
  storageBucket: "multi-cor.firebasestorage.app",
  messagingSenderId: "100513205677",
  appId: "1:100513205677:web:6aea3751ee707b596fe524",
  measurementId: "G-6JSXNEHE5K"
};

/* Ejemplo:
window.PRECIOPIXEL_FIREBASE = {
  apiKey: "AIza...",
  authDomain: "tu-proyecto.firebaseapp.com",
  projectId: "tu-proyecto",
  appId: "1:1234567890:web:abcdef",
};
*/
