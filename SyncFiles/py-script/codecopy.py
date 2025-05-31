# -*- coding: utf-8 -*-
import os

def scan_project_files(root_dir, output_file):
    if not os.path.isdir(root_dir):
        print(f"错误：目录 '{root_dir}' 不存在。")
        return

    with open(output_file, 'w', encoding='utf-8') as outfile:
        print(f"开始扫描目录：{os.path.abspath(root_dir)}\n")
        outfile.write(f"项目根目录：{os.path.abspath(root_dir)}\n\n")
        outfile.write("=" * 80 + "\n") # 大的分隔符

        for dirpath, dirnames, filenames in os.walk(root_dir):
            # 过滤掉一些常见的无需扫描的目录 (可选，根据需要调整)
            dirnames[:] = [d for d in dirnames if d not in ['.git', 'bin', 'obj', '.vs', 'packages', 'node_modules']]

            relevant_files_found_in_dir = False
            temp_dir_output = [] # 临时存储当前目录的文件内容，以便先输出目录名

            for filename in filenames:
                if filename.endswith(('.cs', '.xaml', '.xaml.cs')):
                    if not relevant_files_found_in_dir:
                        relative_dir = os.path.relpath(dirpath, root_dir)
                        if relative_dir == ".":
                            relative_dir = "根目录文件"
                        else:
                            relative_dir = f"文件夹：{relative_dir}"

                        temp_dir_output.append(f"\n--- {relative_dir} ---\n")
                        relevant_files_found_in_dir = True

                    file_path = os.path.join(dirpath, filename)
                    temp_dir_output.append(f"\n{'*' * 10} 文件开始：{filename} ({os.path.relpath(file_path, root_dir)}) {'*' * 10}\n\n")
                    try:
                        with open(file_path, 'r', encoding='utf-8', errors='ignore') as infile:
                            content = infile.read()
                            temp_dir_output.append(content)
                        temp_dir_output.append(f"\n\n{'*' * 10} 文件结束：{filename} {'*' * 10}\n")
                        temp_dir_output.append("-" * 60 + "\n") # 文件间的小分隔符
                    except Exception as e:
                        temp_dir_output.append(f"\n无法读取文件 {filename}: {e}\n")
                        temp_dir_output.append("-" * 60 + "\n")

            if temp_dir_output:
                for item in temp_dir_output:
                    outfile.write(item)
                if relevant_files_found_in_dir:
                    outfile.write("\n") # 当前文件夹内容输出完毕后加一个换行

        outfile.write("\n" + "=" * 80 + "\n")
        outfile.write("扫描完成。\n")
    print(f"\n扫描完成！结果已输出到：{os.path.abspath(output_file)}")

# --- 使用方法 ---
if __name__ == "__main__":
    # 1. 设置你的项目根目录路径
    # 例如: project_root = r"C:\Users\YourUser\Documents\MyAwesomeProject"
    # 或者使用相对路径: project_root = "./MyProject"
    project_root = input("请输入项目根目录的路径：")

    # 2. 设置输出文件名
    output_filename = "project_code_dump.txt"

    # 3. 运行扫描函数
    scan_project_files(project_root, output_filename)