fn main() {
    let args: Vec<String> = std::env::args().collect();
    println!("trimming {:?}", &args[1..]);
}
