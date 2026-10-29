// Seals a password to a collector's RSA public key in the browser (WebCrypto), in the same format as
// Lakeflow.Contracts.SealedSecret (C#): a random AES-256-GCM key encrypts the secret, with the certificate thumbprint
// as associated data, and RSA-OAEP-SHA256 encrypts that key. The password never leaves the browser unencrypted.

const toBase64 = bytes => btoa(String.fromCharCode(...new Uint8Array(bytes)));
const fromBase64 = text => Uint8Array.from(atob(text), c => c.charCodeAt(0));

export async function sealSecret(secret, publicKeySpki, thumbprint) {
    const subtle = globalThis.crypto.subtle;
    const rsa = await subtle.importKey("spki", fromBase64(publicKeySpki), { name: "RSA-OAEP", hash: "SHA-256" }, false, ["encrypt"]);
    const aes = await subtle.generateKey({ name: "AES-GCM", length: 256 }, true, ["encrypt"]);
    const iv = globalThis.crypto.getRandomValues(new Uint8Array(12));
    const encoder = new TextEncoder();
    const sealedBytes = new Uint8Array(await subtle.encrypt(
        { name: "AES-GCM", iv, additionalData: encoder.encode(thumbprint), tagLength: 128 }, aes, encoder.encode(secret)));
    // WebCrypto appends the 16-byte tag to the ciphertext; the format keeps them apart.
    const ct = sealedBytes.slice(0, sealedBytes.length - 16);
    const tag = sealedBytes.slice(sealedBytes.length - 16);
    const rawKey = await subtle.exportKey("raw", aes);
    const ek = await subtle.encrypt({ name: "RSA-OAEP" }, rsa, rawKey);
    const envelope = { v: 1, alg: "RSA-OAEP-256+A256GCM", kid: thumbprint, ek: toBase64(ek), iv: toBase64(iv), ct: toBase64(ct), tag: toBase64(tag) };
    return toBase64(encoder.encode(JSON.stringify(envelope)));
}

// Saves bytes (base64) as a file download.
export function saveFile(name, base64, type) {
    const link = document.createElement("a");
    link.href = URL.createObjectURL(new Blob([fromBase64(base64)], { type }));
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(link.href), 1000);
}
