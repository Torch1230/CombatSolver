use std::env;
use std::path::PathBuf;
use std::process;

fn main() {
    let args: Vec<String> = env::args().collect();
    let mut input: Option<PathBuf> = None;
    let mut output_dir: Option<PathBuf> = None;

    let mut i = 1usize;
    while i < args.len() {
        match args[i].as_str() {
            "--input" if i + 1 < args.len() => {
                input = Some(PathBuf::from(&args[i + 1]));
                i += 2;
            }
            "--output-dir" if i + 1 < args.len() => {
                output_dir = Some(PathBuf::from(&args[i + 1]));
                i += 2;
            }
            "--help" | "-h" => {
                println!("Usage: gen_metadata --input <v011314-metadata.json> --output-dir <dir>");
                return;
            }
            other => {
                eprintln!("Unknown argument: {other}");
                process::exit(2);
            }
        }
    }

    let Some(input_path) = input else {
        eprintln!("Missing required --input <v011314-metadata.json>");
        process::exit(2);
    };
    let Some(out_dir) = output_dir else {
        eprintln!("Missing required --output-dir <dir>");
        process::exit(2);
    };

    match gen_metadata::generate_from_file(&input_path, &out_dir) {
        Ok(code) => {
            println!(
                "Generated {} bytes into {}",
                code.len(),
                out_dir.join("generated_ids.rs").display()
            );
        }
        Err(err) => {
            eprintln!("gen_metadata failed: {err}");
            process::exit(1);
        }
    }
}
