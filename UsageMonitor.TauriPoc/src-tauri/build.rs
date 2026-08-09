fn main() {
    println!("cargo:rerun-if-changed=../dist/index.html");
    println!("cargo:rerun-if-changed=../dist/styles.css");
    println!("cargo:rerun-if-changed=../dist/app.js");
    println!("cargo:rerun-if-changed=../dist/assets");
    println!("cargo:rerun-if-changed=icons/icon.ico");
    println!("cargo:rerun-if-changed=icons/tray.png");
    tauri_build::build()
}
