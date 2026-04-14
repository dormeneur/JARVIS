import subprocess

def run_cmd(cmd, outfile):
    print(f"Running {cmd}...")
    result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, shell=True)
    with open(outfile, 'w', encoding='utf-8') as f:
        f.write(result.stdout)

run_cmd('pytest brain/tests/ -v', 'full_suite.txt')
run_cmd('pytest brain/tests/ -v --tb=short -k "sanitizer or fs_tree"', 'specific_suite.txt')
print("Done.")
