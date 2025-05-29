import os

def remove_comments_and_empty_lines(filepath):
    """
    Removes single-line comments (//) and empty lines from a C# file.

    Args:
        filepath (str): The path to the .cs file.
    """
    try:
        with open(filepath, 'r', encoding='utf-8') as file:
            lines = file.readlines()

        new_lines = []
        for line in lines:
            stripped_line = line.strip()
            if stripped_line == "":  # Skip empty lines
                continue
            if stripped_line.startswith("//"):  # Skip single-line comments
                continue
            new_lines.append(line)

        with open(filepath, 'w', encoding='utf-8') as file:
            file.writelines(new_lines)
        # print(f"Processed: {filepath}")
    except Exception as e:
        print(f"Error processing file {filepath}: {e}")

def process_directory(directory_path):
    """
    Walks through a directory and processes all .cs files.

    Args:
        directory_path (str): The path to the directory.
    """
    if not os.path.isdir(directory_path):
        print(f"Error: Directory not found at '{directory_path}'")
        return

    for root, _, files in os.walk(directory_path):
        for file in files:
            if file.endswith(".cs"):
                file_path = os.path.join(root, file)
                remove_comments_and_empty_lines(file_path)
    print(f"\nProcessing complete for directory: {directory_path}")

if __name__ == "__main__":
    target_directory = input("Enter the directory path containing .cs files: ")
    process_directory(target_directory)