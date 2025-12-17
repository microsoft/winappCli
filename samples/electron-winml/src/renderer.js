// UI elements
const selectImageBtn = document.getElementById('selectImageBtn');
const imagePreview = document.getElementById('imagePreview');
const selectedImage = document.getElementById('selectedImage');
const loadingView = document.getElementById('loadingView');
const resultsContainer = document.getElementById('resultsContainer');
const resultsList = document.getElementById('resultsList');

// Handle image selection
selectImageBtn.addEventListener('click', async () => {
  try {
    // Open file dialog and get selected image path
    const imagePath = await window.electronAPI.selectImage();
    
    if (imagePath) {
      // Show image preview
      selectedImage.src = imagePath;
      imagePreview.classList.remove('hidden');
      
      // Hide previous results
      resultsContainer.classList.add('hidden');
      
      // Show loading view
      loadingView.classList.remove('hidden');
      
      // Send image to main process for classification
      const results = await window.electronAPI.classifyImage(imagePath);
      
      // Hide loading view
      loadingView.classList.add('hidden');
      
      // Display results
      displayResults(results);
    }
  } catch (error) {
    console.error('Error processing image:', error);
    loadingView.classList.add('hidden');
    alert('Error processing image: ' + error.message);
  }
});

// Display classification results
function displayResults(results) {
  // Clear previous results
  resultsList.innerHTML = '';
  
  if (!results || results.length === 0) {
    resultsList.innerHTML = '<li>No results found</li>';
  } else {
    // Sort by confidence (highest first)
    const sortedResults = [...results].sort((a, b) => b.confidence - a.confidence);
    
    // Create list items for each result
    sortedResults.forEach(result => {
      const li = document.createElement('li');
      
      const labelDiv = document.createElement('div');
      labelDiv.style.flex = '1';
      
      const labelName = document.createElement('span');
      labelName.className = 'label-name';
      labelName.textContent = result.label;
      
      const confidenceSpan = document.createElement('span');
      confidenceSpan.className = 'confidence';
      confidenceSpan.textContent = `${(result.confidence * 100).toFixed(1)}%`;
      
      const confidenceBar = document.createElement('div');
      confidenceBar.className = 'confidence-bar';
      
      const confidenceFill = document.createElement('div');
      confidenceFill.className = 'confidence-fill';
      confidenceFill.style.width = `${result.confidence * 100}%`;
      
      confidenceBar.appendChild(confidenceFill);
      labelDiv.appendChild(labelName);
      labelDiv.appendChild(confidenceBar);
      
      li.appendChild(labelDiv);
      li.appendChild(confidenceSpan);
      
      resultsList.appendChild(li);
    });
  }
  
  // Show results container
  resultsContainer.classList.remove('hidden');
}
